using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// A synchronous call between two services, and what a relabel does to it.
    ///
    /// The caller records a CLIENT span naming where it dialled; the callee answers
    /// with a SERVER span, its child, naming the same address. The address comes from
    /// the node id, so changing only a node's label is a rename: a new service.name,
    /// on the same address, with the same callers and dependencies. Nothing else about
    /// the telemetry may change, or the "rename" is something else.
    /// </summary>
    [TestFixture]
    public class ServiceCallShapeTests
    {
        private const string Before = @"
flowchart LR
  web[Storefront] --> api[Orders API x2]
  api --> db[(Orders Postgres)]
  api --> stock[Stock API]
  api --> q[[orders.created]]
  q --> ship[Shipping Worker]";

        // The same diagram with one label changed and every id kept.
        private const string After = @"
flowchart LR
  web[Storefront] --> api[Orders Service x2]
  api --> db[(Orders Postgres)]
  api --> stock[Stock API]
  api --> q[[orders.created]]
  q --> ship[Shipping Worker]";

        private static List<Activity> Capture(string diagram, int run = 1)
        {
            using var pool = new PodTracerPool(target: null);
            return CaptureWith(diagram, pool, run);
        }

        private static List<Activity> CaptureWith(string diagram, PodTracerPool pool, int run = 1) =>
            Fire(diagram, pool, run).Spans;

        /// <summary>One run, with its result and the spans of its own trace.</summary>
        private static (RunResult Result, List<Activity> Spans) Fire(string diagram, PodTracerPool pool, int run = 1)
        {
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), run, "test");
            return (result, captured.Where(a => a.TraceId.ToString() == result.TraceId).ToList());
        }

        // A pod's ActivitySource is named by its pod name, {service}-{templateHash}-{suffix}
        // (Pod.InstanceIdOf). Matched as that whole shape, so a change to the format
        // fails here loudly instead of slicing a wrong service name.
        private static readonly System.Text.RegularExpressions.Regex PodName = new(
            $"^(?<service>.+)-[{Pod.SafeAlphabet}]{{{Pod.TemplateHashLength}}}-[{Pod.SafeAlphabet}]{{{Pod.SuffixLength}}}$");

        private static string ServiceOf(Activity span)
        {
            var match = PodName.Match(span.Source.Name);
            if (!match.Success) throw new InvalidOperationException($"{span.Source.Name} is not a pod name");
            return match.Groups["service"].Value;
        }

        // Every run here is fired in shoebox "test", so its services answer in that
        // shoebox's namespace.
        private static string Host(string id) => Pod.HostOf(id, Pod.NamespaceOf("test"));

        [Test]
        public void Two_Shoeboxes_With_The_Same_Diagram_Answer_On_Different_Addresses()
        {
            // Everybody draws a node called api. Without a namespace per shoebox, two
            // visitors sending to one backend would share its address, and one of them
            // relabelling theirs would read as a rename of the other's.
            using var pool = new PodTracerPool(target: null);
            var runner = new TopologyRunner(pool);
            string? AddressIn(string? shoebox)
            {
                var captured = new List<Activity>();
                using var listener = new ActivityListener
                {
                    ShouldListenTo = _ => true,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = captured.Add,
                };
                ActivitySource.AddActivityListener(listener);
                var result = runner.Run(MermaidParser.Parse(Before), 1, shoebox);
                return captured.Single(s => s.TraceId.ToString() == result.TraceId && s.Kind == ActivityKind.Server
                                            && ServiceOf(s) == "orders-api").GetTagItem("server.address") as string;
            }

            var mine = AddressIn("visitor-one");
            var theirs = AddressIn("visitor-two");

            mine.Should().NotBe(theirs);
            mine.Should().Be(Pod.HostOf("api", Pod.NamespaceOf("visitor-one")));
            AddressIn("visitor-one").Should().Be(mine, "the same shoebox answers on the same address every run");
            AddressIn(null).Should().Be("api.default.svc.cluster.local", "a run with no shoebox uses the default namespace");
        }

        [TestCase("visitor-one")]
        [TestCase("8f14e45fceea167a5a36dedd4bea2543")]
        [TestCase("Ünïcødé / with spaces\r\nand.dots")]
        [TestCase("a very long shoebox id that goes on and on well past anything a DNS label could ever hold in one piece")]
        public void A_Namespace_Is_A_Dns_Label_That_Does_Not_Carry_The_Shoebox_Id(string shoebox)
        {
            var ns = Pod.NamespaceOf(shoebox);

            ns.Should().MatchRegex("^sb-[a-z0-9]{10}$");
            ns.Should().NotContain(shoebox.ToLowerInvariant(), "the id is hashed, never written into an address");
            Pod.NamespaceOf(shoebox).Should().Be(ns, "deterministic");
        }

        [Test]
        public void At_The_Budget_A_Queue_Is_Not_Published_Without_Its_Consumer()
        {
            // Entry SERVER plus 249 calls is 499 spans. A publish with a consumer
            // drawn needs two; with one left the walk stops before publishing, rather
            // than end on a publish nothing received, which reads as unconsumed.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i <= 249; i++) lines.Add($"  entry[Entry] --> s{i}[S{i}]");
            lines.Add("  entry --> q[[Jobs]]");
            lines.Add("  q --> w[Worker]");

            using var pool = new PodTracerPool(target: null);
            var (result, spans) = Fire(string.Join("\n", lines), pool);

            result.SpanCount.Should().Be(RunLimits.MaxSpans - 1);
            spans.Should().NotContain(s => s.Kind == ActivityKind.Producer);
            result.Hops.Should().NotContain(h => h.To == "q" || h.To == "w");
            result.NotTaken.Should().Contain(n => n.From == "entry" && n.To == "q" && n.Reason.StartsWith("not walked"));
        }

        [Test]
        public void Past_The_Depth_Limit_A_Queue_Is_Not_Published_Without_Its_Consumer()
        {
            // s33 is at depth 32, so whatever consumes its publish would be at 33,
            // past the limit: the publish is not emitted and is not a hop.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i <= 32; i++) lines.Add($"  s{i}[S{i}] --> s{i + 1}[S{i + 1}]");
            lines.Add("  s33 --> q[[Jobs]]");
            lines.Add("  q --> w[Worker]");

            using var pool = new PodTracerPool(target: null);
            var (result, spans) = Fire(string.Join("\n", lines), pool);

            spans.Should().NotContain(s => s.Kind == ActivityKind.Producer);
            result.Hops.Should().NotContain(h => h.To == "q" || h.To == "w");
            result.Notes.Should().Contain(n => n.Contains($"depth {RunLimits.MaxDepth}"));
            result.NotTaken.Should().Contain(n => n.From == "s33" && n.To == "q" && n.Reason.StartsWith("not walked"));
        }

        [Test]
        public void A_Cut_Run_Says_It_Stopped_Not_That_There_Was_No_Path()
        {
            // An edge the run stopped short of is reachable; one out of a pod nothing
            // arrives at is not. The two are reported differently.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i <= 260; i++) lines.Add($"  entry[Entry] --> s{i}[S{i}]");
            lines.Add("  island[Island] --> elsewhere[Elsewhere]");
            lines.Add("  other[Other] --> island");

            using var pool = new PodTracerPool(target: null);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(string.Join("\n", lines)), 1, "test");

            result.NotTaken.Should().Contain(n => n.To == "s260" && n.Reason.StartsWith("not walked"));
            result.NotTaken.Should().NotContain(n => n.To == "s260" && n.Reason.StartsWith("never reached"));
            result.NotTaken.Should().Contain(n => n.From == "island" && n.To == "elsewhere" && n.Reason.StartsWith("never reached"),
                "nothing from the entry arrives at island, cut run or not");
        }

        [Test]
        public void A_Cut_Run_Does_Not_Blame_The_Limit_For_Edges_It_Was_Never_Going_To_Take()
        {
            // The far side of a broken call and a phantom consumer are reached and
            // declined before the cut. The limit had nothing to do with them, so
            // they read exactly as in a run that was not cut.
            const string head = @"
flowchart LR
  entry[Entry] -->|broken: connection refused| b[B]
  b --> c[C]
  entry --> q[[Q]]
  q -->|phantom| ghost[Ghost]
  ghost --> g2[G2]";
            var fan = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"  entry --> s{i}[S{i}]"));

            using var pool = new PodTracerPool(target: null);
            var cut = new TopologyRunner(pool).Run(MermaidParser.Parse(head + "\n" + fan), 1, "test");
            var uncut = new TopologyRunner(pool).Run(MermaidParser.Parse(head), 1, "test");

            cut.Notes.Should().Contain(n => n.Contains($"{RunLimits.MaxSpans} spans"), "this run must be cut");
            foreach (var (from, to) in new[] { ("b", "c"), ("q", "ghost"), ("ghost", "g2") })
            {
                cut.NotTaken.Where(n => n.From == from && n.To == to).Select(n => n.Reason)
                    .Should().BeEquivalentTo(uncut.NotTaken.Where(n => n.From == from && n.To == to).Select(n => n.Reason),
                        $"{from}->{to} reads the same whether or not the run was cut");
            }

            cut.NotTaken.Should().Contain(n => n.From == "entry" && n.To == "s300" && n.Reason.StartsWith("not walked: the run stopped at its budget"));
        }

        [Test]
        public void Any_Node_Id_And_Any_Shoebox_Id_Give_A_Valid_Host_In_Its_Own_Namespace()
        {
            // A seeded property check over ids anyone could paste or send: any
            // character, any length. The address is always a valid DNS name from a
            // closed alphabet, the same every time, and distinct well-formed ids or
            // shoebox ids never land on one host or one namespace.
            var random = new Random(20261124);
            // Whole characters, an emoji included as one: a lone surrogate cannot
            // arrive, since node ids are ASCII by the parser's grammar and a shoebox
            // id comes URL-decoded.
            var alphabet = new[] { "a", "A", "z", "Z", "0", "9", "_", "-", ".", " ", "/", ":", "@", "%", "\r", "\n", "\t", "é", "中", "\U0001F525" };
            string RandomId() => string.Concat(Enumerable.Range(0, random.Next(0, 90)).Select(_ => alphabet[random.Next(alphabet.Length)]));

            var hostPattern = new System.Text.RegularExpressions.Regex(
                @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.(default|sb-[a-z0-9]{10})\.svc\.cluster\.local$");
            var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
            var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < 20_000; i++)
            {
                var id = RandomId();
                var shoebox = RandomId();
                var ns = Pod.NamespaceOf(shoebox);
                var host = Pod.HostOf(id, ns);

                host.Should().MatchRegex(hostPattern.ToString());
                Pod.HostOf(id, Pod.NamespaceOf(shoebox)).Should().Be(host, "deterministic");
                if (shoebox.Trim().Length > 3) ns.Should().NotContain(shoebox.ToLowerInvariant().Trim());

                if (hosts.TryGetValue(Pod.HostLabelOf(id), out var other)) other.Should().Be(id, "two ids, one host");
                else hosts[Pod.HostLabelOf(id)] = id;
                if (namespaces.TryGetValue(ns, out var otherShoebox)) otherShoebox.Should().Be(shoebox, "two shoeboxes, one namespace");
                else namespaces[ns] = shoebox;
            }
        }

        [Test]
        public void A_Whitespace_Shoebox_Id_Is_A_Shoebox_Of_Its_Own()
        {
            // The rate limiter treats any non-empty id as a shoebox; so does the namespace.
            Pod.NamespaceOf(" ").Should().MatchRegex("^sb-[a-z0-9]{10}$");
            Pod.NamespaceOf(" ").Should().NotBe(Pod.NamespaceOf("  "));
            Pod.NamespaceOf("").Should().Be(Pod.DefaultNamespace);
            Pod.NamespaceOf(null).Should().Be(Pod.DefaultNamespace);
        }

        [Test]
        public void A_Cut_Never_Splits_A_Character()
        {
            // An emoji is two UTF-16 units; one straddling the cut is dropped whole.
            var reason = new string('x', MermaidParser.MaxFailureReasonLength - 4) + "\U0001F525" + new string('y', 50);
            var call = MermaidParser.Parse($"flowchart LR\n  a[A] -->|broken: {reason}| b[(B)]").Calls.Single();

            call.FailureReason!.Length.Should().BeLessThanOrEqualTo(MermaidParser.MaxFailureReasonLength);
            call.FailureReason.Should().EndWith("...");
            call.FailureReason.Where(char.IsSurrogate).Count().Should().Be(0, "half an emoji is not left behind");
        }

        [Test]
        public void Concurrent_Callers_Get_One_Pod_And_A_Disposed_Pool_Builds_No_More()
        {
            var pool = new PodTracerPool(target: null);
            var sources = new System.Collections.Concurrent.ConcurrentBag<ActivitySource>();
            Parallel.For(0, 64, _ => sources.Add(pool.For("orders-api", 1)));

            sources.Distinct().Should().ContainSingle("every caller gets the one source of the one pod");
            pool.Builds.Should().Be(1, "a race builds the pod once, not once per caller that lost it");

            pool.Dispose();
            var after = () => pool.For("orders-api", 2);
            after.Should().Throw<ObjectDisposedException>();
        }

        [Test]
        public void A_Long_Failure_Reason_Is_Cut_Before_It_Is_Copied_Onto_Spans()
        {
            var reason = new string('x', 5000);
            var spans = Capture($"flowchart LR\n  web[Storefront] --> api[Orders API]\n  api -->|broken: {reason}| db[(SQL Server)]");

            spans.Where(s => s.Status == ActivityStatusCode.Error)
                .Should().NotBeEmpty()
                .And.OnlyContain(s => s.StatusDescription!.Length <= MermaidParser.MaxFailureReasonLength);
        }

        [Test]
        public void A_Call_Is_A_Client_Span_Under_The_Caller_And_A_Server_Span_Under_The_Callee()
        {
            var spans = Capture(Before);
            var host = Host("api");

            var client = spans.Single(s => s.Kind == ActivityKind.Client && Equals(s.GetTagItem("server.address"), host));
            ServiceOf(client).Should().Be("storefront");
            client.GetTagItem("server.port").Should().Be(Pod.ServicePort);
            client.GetTagItem("http.request.method").Should().Be("GET");

            var server = spans.Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-api");
            server.ParentSpanId.Should().Be(client.SpanId, "the callee answers the caller's call, as its child");
            server.GetTagItem("server.address").Should().Be(host, "the callee answers on the host the caller dialled");
            server.GetTagItem("server.port").Should().Be(Pod.ServicePort);
            server.GetTagItem("http.route").Should().Be("/api");
        }

        [Test]
        public void The_Entry_Service_Says_Where_It_Answers_Too()
        {
            var entry = Capture(Before).Single(s => s.ParentSpanId == default);

            entry.Kind.Should().Be(ActivityKind.Server);
            entry.GetTagItem("server.address").Should().Be(Host("web"));
        }

        [Test]
        public void Every_Service_Call_Has_Exactly_One_Answer_On_The_Same_Address()
        {
            // A dialled host nothing answers on reads as a peer nobody has seen, so
            // every successful call's host is answered by exactly one SERVER span,
            // its child.
            var spans = Capture(Before);
            var calls = spans.Where(s => s.Kind == ActivityKind.Client && s.GetTagItem("url.full") is not null).ToList();

            calls.Should().HaveCount(2, "storefront calls api, api calls stock");
            foreach (var call in calls)
            {
                spans.Where(s => s.ParentSpanId == call.SpanId).Should().ContainSingle()
                    .Which.Should().Match<Activity>(s => s.Kind == ActivityKind.Server
                                                         && Equals(s.GetTagItem("server.address"), call.GetTagItem("server.address")));
            }
        }

        [Test]
        public void A_Relabel_Changes_The_Name_And_Keeps_The_Address()
        {
            var before = Capture(Before).Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-api");
            var after = Capture(After).Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-service");

            after.GetTagItem("server.address").Should().Be(before.GetTagItem("server.address"));
            after.GetTagItem("http.route").Should().Be(before.GetTagItem("http.route"));
        }

        [Test]
        public void A_Staged_Rename_On_A_Timer_Hands_Over_On_One_Address()
        {
            // What the staged rename is: firing on a timer, one process, the diagram
            // edited mid-stream. Runs 1 to 10 fire the old label, 11 to 20 the new.
            // The old name stops exactly at the edit, the new one starts there, and
            // every run, before and after, is called by the same caller on the same
            // address and calls the same things.
            using var pool = new PodTracerPool(target: null);
            var runs = Enumerable.Range(1, 20)
                .Select(run => (Run: run, Spans: CaptureWith(run <= 10 ? Before : After, pool, run)))
                .ToList();

            foreach (var (run, spans) in runs)
            {
                var server = spans.Single(s => s.Kind == ActivityKind.Server && s.GetTagItem("server.address") as string == Host("api"));
                ServiceOf(server).Should().Be(run <= 10 ? "orders-api" : "orders-service", $"run {run}");
                ServiceOf(spans.Single(s => s.SpanId == server.ParentSpanId)).Should().Be("storefront", $"run {run}");
                spans.Where(s => s.ParentSpanId == server.SpanId)
                    .Select(s => (string?)(s.GetTagItem("server.address") ?? s.GetTagItem("messaging.destination.name")))
                    .Should().BeEquivalentTo(new[] { "orders-postgres", Host("stock"), "orders-created" }, $"run {run}");
            }

            runs.SelectMany(r => r.Spans).Select(ServiceOf).Distinct()
                .Should().BeEquivalentTo("storefront", "orders-api", "orders-service", "stock-api", "shipping-worker");
        }

        [Test]
        public void A_Staged_Rename_Hands_Over_In_Time_Not_Only_In_Order()
        {
            // The ground truth for a staged rename is old label, new label, time.
            // Fired as the timer fires them, the old name's last span ends before
            // the new name's first span starts: the handover is a moment, not an
            // overlap, which is what lets anything reading the spans put the
            // rename at the edit.
            //
            // A run's spans are timed by the model, so a run lasts as long as its
            // modelled latencies add up to, starting from when it was fired. Fired
            // faster than that, runs overlap in time; the timer's slowest gap
            // between runs is seconds, far longer than any run here. The test
            // fires at a gap longer than one run's modelled length, as the timer does.
            using var pool = new PodTracerPool(target: null);
            var spans = new List<Activity>();
            for (var run = 1; run <= 6; run++)
            {
                var fired = CaptureWith(run <= 3 ? Before : After, pool, run);
                spans.AddRange(fired);
                var runLength = fired.Max(s => s.StartTimeUtc + s.Duration) - fired.Min(s => s.StartTimeUtc);
                Thread.Sleep(runLength + TimeSpan.FromMilliseconds(20));
            }

            var oldLast = spans.Where(s => ServiceOf(s) == "orders-api").Max(s => s.StartTimeUtc + s.Duration);
            var newFirst = spans.Where(s => ServiceOf(s) == "orders-service").Min(s => s.StartTimeUtc);

            newFirst.Should().BeAfter(oldLast, "the new name starts after the old one has stopped");
        }

        [Test]
        public void A_Whitespace_Shoebox_Id_Is_Carried_On_Its_Spans_Too()
        {
            // The namespace treats " " as a shoebox, so the spans say which one.
            using var pool = new PodTracerPool(target: null);
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(Before), 1, " ");

            captured.Where(s => s.TraceId.ToString() == result.TraceId)
                .Should().OnlyContain(s => (string?)s.GetTagItem("shoebox.id") == " ");
        }

        [Test]
        public void A_Relabel_Keeps_The_Same_Callers_And_Dependencies()
        {
            // What a rename detector compares besides the address: who calls the
            // service, and what it calls. Both must be identical across the relabel,
            // or the pair reads as two different services.
            static (string Callers, string Deps) Peers(List<Activity> spans, string service)
            {
                var server = spans.Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == service);
                var callerSpan = spans.Single(s => s.SpanId == server.ParentSpanId);
                var deps = spans
                    .Where(s => ServiceOf(s) == service && s.Kind is ActivityKind.Client or ActivityKind.Producer)
                    .Select(s => $"{s.Kind}:{s.GetTagItem("server.address") ?? s.GetTagItem("messaging.destination.name")}")
                    .OrderBy(x => x, StringComparer.Ordinal);
                return (ServiceOf(callerSpan), string.Join(",", deps));
            }

            var before = Peers(Capture(Before), "orders-api");
            var after = Peers(Capture(After), "orders-service");

            after.Should().Be(before);
            before.Deps.Should().Contain(Host("stock")).And.Contain("orders-postgres").And.Contain("orders-created");
        }

        [Test]
        public void A_Relabel_Changes_Nothing_Else_In_The_Trace()
        {
            // Every span of the run, reduced to its shape with the renamed service's
            // name masked: kind, name, parent's position, and every attribute. Only
            // service.name may differ.
            // service.instance.id is masked the same way: a pod is named after its
            // service, so its name moves with the rename, and only that part may.
            static List<string> Shape(List<Activity> spans, string renamed)
            {
                string Mask(object? value) => (value?.ToString() ?? "").Replace(renamed, "RENAMED");

                // Ordered by everything except the raw names, so the order itself
                // cannot differ just because the renamed name sorts differently.
                var order = spans
                    .OrderBy(s => s.StartTimeUtc)
                    .ThenBy(s => s.Kind)
                    .ThenBy(s => Mask(s.DisplayName), StringComparer.Ordinal)
                    .ThenBy(s => Mask(s.GetTagItem("server.address") ?? s.GetTagItem("messaging.destination.name")), StringComparer.Ordinal)
                    .ToList();
                var index = order.Select((s, i) => (s.SpanId, i)).ToDictionary(p => p.SpanId, p => p.i);
                return order.Select(s =>
                {
                    var parent = s.ParentSpanId == default ? -1 : index[s.ParentSpanId];
                    // A pod name hashes its service's name, so the renamed service's
                    // pods are wholly new names; that is checked on its own below.
                    var tags = string.Join(";", s.TagObjects
                        .OrderBy(t => t.Key, StringComparer.Ordinal)
                        .Select(t => t.Key == "service.instance.id" && Mask(t.Value).StartsWith("RENAMED-")
                            ? $"{t.Key}=RENAMED-POD"
                            : $"{t.Key}={Mask(t.Value)}"));
                    return $"{Mask(ServiceOf(s))}|{s.Kind}|{Mask(s.DisplayName)}|{parent}|{s.Status}|{tags}";
                }).ToList();
            }

            var before = Capture(Before);
            var after = Capture(After);
            Shape(after, "orders-service").Should().Equal(Shape(before, "orders-api"));

            // And the pod names did move with it: the renamed service's pods are new
            // names, every other service's pods are the same ones.
            static IEnumerable<string> Pods(List<Activity> spans, Func<string, bool> which) => spans
                .Where(s => which(ServiceOf(s)) && s.GetTagItem("service.instance.id") is not null)
                .Select(s => (string)s.GetTagItem("service.instance.id")!)
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal);

            Pods(after, s => s == "orders-service").Should().OnlyContain(p => p.StartsWith("orders-service-"))
                .And.NotIntersectWith(Pods(before, s => s == "orders-api"));
            Pods(after, s => s != "orders-service").Should().Equal(Pods(before, s => s != "orders-api"));
        }

        [Test]
        public void Queues_Databases_And_Failed_Calls_Keep_Their_Shape()
        {
            var spans = Capture(Before);

            spans.Single(s => s.Kind == ActivityKind.Consumer).GetTagItem("server.address")
                .Should().BeNull("a consumer is a messaging span, not an HTTP server");
            spans.Single(s => s.Kind == ActivityKind.Producer).GetTagItem("server.address").Should().BeNull();
            spans.Single(s => s.GetTagItem("db.system.name") is not null).GetTagItem("server.address")
                .Should().Be("orders-postgres", "the database is named by its label, as before");

            var failed = Capture(@"
flowchart LR
  web[Storefront] -->|broken: connection refused| api[Orders API]");
            var call = failed.Single(s => s.Kind == ActivityKind.Client);
            call.Status.Should().Be(ActivityStatusCode.Error);
            call.GetTagItem("server.address").Should().BeNull("a failed call is emitted exactly as before");
        }

        [Test]
        public void A_Failure_Behind_The_Call_Shows_On_The_Callers_Client_Span()
        {
            var spans = Capture(@"
flowchart LR
  web[Storefront] --> api[Orders API]
  api -->|broken: wrong column| db[(SQL Server)]");

            var call = spans.Single(s => Equals(s.GetTagItem("server.address"), Host("api")) && s.Kind == ActivityKind.Client);
            call.Status.Should().Be(ActivityStatusCode.Error);
            call.GetTagItem("http.response.status_code").Should().Be(500);
            call.GetTagItem("error.type").Should().Be("500", "the HTTP conventions name the status code as the error type");

            // Both halves of the call agree on the outcome and the cause.
            var answer = spans.Single(s => s.ParentSpanId == call.SpanId);
            answer.Kind.Should().Be(ActivityKind.Server);
            answer.Status.Should().Be(ActivityStatusCode.Error);
            answer.GetTagItem("http.response.status_code").Should().Be(500);
            answer.StatusDescription.Should().Be(call.StatusDescription);
        }

        [TestCase("api", "api.default.svc.cluster.local")]
        [TestCase("orders-api", "orders-api.default.svc.cluster.local")]
        public void A_Dns_Safe_Id_Is_Its_Own_Host(string id, string host) =>
            Pod.HostOf(id).Should().Be(host);

        [Test]
        public void Ids_That_Differ_Only_In_Case_Or_Punctuation_Get_Different_Hosts()
        {
            // Mermaid ids are case sensitive, so these are four nodes. One host for
            // two of them would make two services one address.
            var hosts = new[] { "api", "Api", "API", "a_pi" }.Select(id => Pod.HostOf(id)).ToList();

            hosts.Should().OnlyHaveUniqueItems();
            hosts.Should().OnlyContain(h => System.Text.RegularExpressions.Regex.IsMatch(
                h, @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.default\.svc\.cluster\.local$"));
            // Pinned from a run of HostOf as written: the suffix is SHA-256 of "host|Api"
            // in the Kubernetes name alphabet. If this changes, every shared link's hosts
            // moved, and a backend would see each service on a new address.
            Pod.HostOf("Api").Should().Be("api-qrx24f7l.default.svc.cluster.local", "deterministic, so a shared link names the same host everywhere");
            Pod.HostOf("").Should().MatchRegex(@"^svc-[a-z0-9]{8}\.default\.svc\.cluster\.local$", "an empty id still gets a valid host");
        }

        [Test]
        public void A_Long_Id_Still_Makes_A_Valid_Dns_Label()
        {
            var label = Pod.HostOf(new string('X', 200)).Split('.')[0];
            label.Length.Should().BeLessThanOrEqualTo(63);
        }

        [TestCase("api[Orders API x2]", null, "1.0.0")]
        [TestCase("api[Orders API x2 rev2]", 2, "1.2.0")]
        [TestCase("api[Orders API rev10 x2]", 10, "1.10.0")]
        public void Service_Version_Follows_The_Rev_Marker(string node, int? revision, string version)
        {
            using var pool = new PodTracerPool(target: null);
            CaptureWith($"flowchart LR\n  {node} --> db[(Postgres)]", pool);

            var resource = pool.ResourceOf("orders-api", 1, revision);
            resource.Should().NotBeNull();
            resource!.Attributes.Should().Contain(new KeyValuePair<string, object>("service.version", version));
            resource.Attributes.Should().Contain(new KeyValuePair<string, object>("service.name", "orders-api"));
        }

        [Test]
        public void Every_Pod_That_Emitted_Reports_Its_Own_Version()
        {
            // Every service in the diagram, every replica that served, each at its
            // own marker: one service's rev never leaks into another's version.
            using var pool = new PodTracerPool(target: null);
            const string diagram = @"
flowchart LR
  web[Storefront] --> api[Orders API x2 rev3]
  api --> stock[Stock API rev1]
  api --> q[[orders.created]]
  q --> ship[Shipping Worker]";
            var expected = new Dictionary<string, (int? Rev, int Replicas)>
            {
                ["storefront"] = (null, 1),
                ["orders-api"] = (3, 2),
                ["stock-api"] = (1, 1),
                ["shipping-worker"] = (null, 1),
            };

            CaptureWith(diagram, pool, run: 1);
            CaptureWith(diagram, pool, run: 2);

            foreach (var (service, (rev, replicas)) in expected)
            {
                for (var n = 1; n <= replicas; n++)
                {
                    var resource = pool.ResourceOf(service, n, rev);
                    resource.Should().NotBeNull($"{service} #{n} served a run");
                    resource!.Attributes.Should().Contain(new KeyValuePair<string, object>("service.version", Pod.VersionOf(rev)),
                        $"{service} is at rev {rev?.ToString() ?? "none"}");
                }
            }
        }

        [Test]
        public void A_Rev_Bump_Is_A_New_Version_On_The_Same_Address()
        {
            using var beforePool = new PodTracerPool(target: null);
            using var afterPool = new PodTracerPool(target: null);
            var before = CaptureWith("flowchart LR\n  web[Storefront] --> api[Orders API x2]", beforePool);
            var after = CaptureWith("flowchart LR\n  web[Storefront] --> api[Orders API x2 rev1]", afterPool);

            beforePool.ResourceOf("orders-api", 1)!.Attributes
                .Should().Contain(new KeyValuePair<string, object>("service.version", "1.0.0"));
            afterPool.ResourceOf("orders-api", 1, revision: 1)!.Attributes
                .Should().Contain(new KeyValuePair<string, object>("service.version", "1.1.0"));

            after.Single(s => ServiceOf(s) == "orders-api").GetTagItem("server.address")
                .Should().Be(before.Single(s => ServiceOf(s) == "orders-api").GetTagItem("server.address"));
        }

        [Test]
        public void A_Pinned_Pod_Answers_On_Its_Nodes_Host()
        {
            // "#2" names a pod, not an address: a pinned pod answers on its node's host
            // like any other, and two pinned nodes of one service are two hosts.
            var spans = Capture(@"
flowchart LR
  web[Storefront] --> w1[Worker #1]
  web --> w2[Worker #2]");

            spans.Where(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "worker")
                .Select(s => s.GetTagItem("server.address"))
                .Should().BeEquivalentTo(new object[] { Host("w1"), Host("w2") });
        }

        [Test]
        public void A_Valid_Dns_Label_Of_63_Characters_Is_Used_As_It_Is()
        {
            var id = new string('a', 63);
            Pod.HostOf(id).Should().Be($"{id}.default.svc.cluster.local");
        }

        [TestCase(64)]
        [TestCase(200)]
        public void A_Longer_Id_Is_Cut_And_Suffixed_Into_A_Valid_Label(int length)
        {
            var id = new string('a', length);
            var label = Pod.HostOf(id).Split('.')[0];

            label.Length.Should().BeLessThanOrEqualTo(63);
            System.Text.RegularExpressions.Regex.IsMatch(label, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$").Should().BeTrue();
            Pod.HostOf(id).Should().NotBe(Pod.HostOf(new string('a', 63)), "a cut id must not collide with one that fits");
            Pod.HostOf(id).Should().NotBe(Pod.HostOf(new string('a', length + 1)), "two cut ids must not collide");
        }

        [Test]
        public void With_One_Span_Left_The_Walk_Stops_Rather_Than_Visiting_The_Rest()
        {
            // Entry SERVER (1) plus 249 calls of two spans each is 499. The 250th
            // service call does not fit, and the walk stops there: nothing after it
            // runs, not even the one-span database call. Skipping only that call
            // would leave the count at 499, where the budget never trips, and the
            // walk would go on visiting every remaining edge of the diagram.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i <= 250; i++) lines.Add($"  entry[Entry] --> s{i}[S{i}]");
            lines.Add("  entry --> db[(Postgres)]");
            for (var i = 251; i <= 2000; i++) lines.Add($"  entry --> s{i}[S{i}]");

            using var pool = new PodTracerPool(target: null);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(string.Join("\n", lines)), 1, "test");

            result.SpanCount.Should().Be(RunLimits.MaxSpans - 1);
            result.Hops.Should().HaveCount(249, "only the calls made in full");
            result.Hops.Should().NotContain(h => h.To == "db", "the walk stopped before it");
            result.Notes.Should().Contain(n => n.Contains($"{RunLimits.MaxSpans} spans"));
        }

        [Test]
        public void A_Chain_Past_The_Depth_Limit_Stops_At_A_Whole_Call()
        {
            // 40 services in a line, no cycle: the walk stops at depth 32. The last
            // call is not emitted at all, rather than as a CLIENT span nothing
            // answers, and the hops are exactly the calls that were.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i < 40; i++) lines.Add($"  s{i}[S{i}] --> s{i + 1}[S{i + 1}]");

            using var pool = new PodTracerPool(target: null);
            var (result, spans) = Fire(string.Join("\n", lines), pool);

            var calls = spans.Where(s => s.Kind == ActivityKind.Client).ToList();
            calls.Should().HaveCount(RunLimits.MaxDepth, "s1 at depth 0 calls down to depth 32 and no further");
            foreach (var call in calls)
            {
                spans.Should().Contain(s => s.ParentSpanId == call.SpanId && s.Kind == ActivityKind.Server);
            }

            result.Hops.Should().HaveCount(calls.Count);
            result.Notes.Should().Contain(n => n.Contains($"depth {RunLimits.MaxDepth}"))
                .And.NotContain(n => n.Contains("cycle"), "this chain has none");
        }

        [Test]
        public void A_Phantom_Marker_On_A_Direct_Call_Is_An_Ordinary_Answered_Call()
        {
            // A phantom needs a queue; on a direct edge it is ignored with a note and
            // runs as a call. It goes through the same path as any call, so it is
            // the same CLIENT and SERVER pair on the callee's address.
            using var pool = new PodTracerPool(target: null);
            var (result, spans) = Fire("flowchart LR\n  orders[Orders API] -->|phantom| pay[Payment Service]", pool);

            var call = spans.Single(s => s.Kind == ActivityKind.Client);
            call.GetTagItem("server.address").Should().Be(Host("pay"));
            var answer = spans.Single(s => s.ParentSpanId == call.SpanId);
            answer.Kind.Should().Be(ActivityKind.Server);
            ServiceOf(answer).Should().Be("payment-service");
            answer.GetTagItem("server.address").Should().Be(Host("pay"));
            result.Hops.Should().ContainSingle(h => h.From == "orders" && h.To == "pay");
        }

        [Test]
        public void A_Server_Span_Says_What_It_Answered()
        {
            var spans = Capture(@"
flowchart LR
  web[Storefront] --> api[Orders API]
  web --> stock[Stock API]
  api -->|broken: wrong column| db[(SQL Server)]");

            spans.Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-api")
                .GetTagItem("http.response.status_code").Should().Be(500);
            spans.Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "stock-api")
                .GetTagItem("http.response.status_code").Should().Be(200);
        }

        [Test]
        public void A_Run_That_Reaches_The_Budget_Without_A_Cycle_Does_Not_Blame_One()
        {
            // Wide, not cyclic: one entry calling 300 services is 601 spans.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i <= 300; i++) lines.Add($"  entry[Entry] --> s{i}[S{i}]");

            using var pool = new PodTracerPool(target: null);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(string.Join("\n", lines)), 1, "test");

            result.SpanCount.Should().BeLessThanOrEqualTo(RunLimits.MaxSpans);
            result.Notes.Should().Contain(n => n.Contains($"{RunLimits.MaxSpans} spans"))
                .And.NotContain(n => n.Contains("cycle"), "this diagram has none");
            result.Hops.Should().HaveCount((RunLimits.MaxSpans - 1) / 2, "a hop is recorded only for a call that was emitted");
        }

        [Test]
        public void A_Run_Cut_By_The_Span_Budget_Leaves_No_Unanswered_Call()
        {
            // A call is two spans. When a run reaches the cap, the last call must
            // not be a CLIENT span with no SERVER answering it: that is a dialled
            // host nothing answers on, which reads as a peer nobody has seen. Each
            // mesh size lands the budget on a different span, so between them the
            // cap falls both on a call's first half and between calls.
            for (var size = 6; size <= 8; size++)
            {
                var ids = Enumerable.Range(1, size).Select(i => $"s{i}").ToList();
                var lines = new List<string> { "flowchart LR", "  entry[Entry] --> s1[S1]" };
                foreach (var a in ids)
                foreach (var b in ids.Where(b => b != a))
                    lines.Add($"  {a}[{a.ToUpperInvariant()}] --> {b}[{b.ToUpperInvariant()}]");

                using var pool = new PodTracerPool(target: null);
                var (result, spans) = Fire(string.Join("\n", lines), pool);

                result.Notes.Should().Contain(n => n.Contains($"{RunLimits.MaxSpans} spans"), $"a {size}-node mesh must reach the cap");
                result.SpanCount.Should().BeLessThanOrEqualTo(RunLimits.MaxSpans);
                var calls = spans.Where(s => s.Kind == ActivityKind.Client && s.GetTagItem("url.full") is not null).ToList();
                foreach (var call in calls)
                {
                    spans.Should().Contain(s => s.ParentSpanId == call.SpanId && s.Kind == ActivityKind.Server,
                        $"every call is answered, even in a {size}-node run cut at the cap");
                }

                result.Hops.Should().HaveCount(calls.Count, "the client replays hops as the path taken, so each is an emitted call");
            }
        }

        [Test]
        public void The_Route_Is_The_Hosts_First_Label()
        {
            var spans = Capture("flowchart LR\n  web[Storefront] --> order_api[Orders API]");
            var server = spans.Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-api");
            var host = Host("order_api");
            var route = $"/{host.Split('.')[0]}";

            route.Should().MatchRegex("^/order-api-[a-z0-9]{8}$", "a suffixed id keeps its suffix in the route");
            server.GetTagItem("http.route").Should().Be(route);
            spans.Single(s => s.SpanId == server.ParentSpanId).GetTagItem("url.full")
                .Should().Be($"http://{host}:{Pod.ServicePort}{route}", "the caller dials the route the callee answers on");
        }
    }
}
