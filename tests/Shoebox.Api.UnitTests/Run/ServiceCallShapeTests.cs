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

        private static List<Activity> CaptureWith(string diagram, PodTracerPool pool, int run = 1)
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
            return captured.Where(a => a.TraceId.ToString() == result.TraceId).ToList();
        }

        private static string ServiceOf(Activity span) =>
            span.Source.Name[..(span.Source.Name.Length - Pod.TemplateHashLength - Pod.SuffixLength - 2)];

        [Test]
        public void A_Call_Is_A_Client_Span_Under_The_Caller_And_A_Server_Span_Under_The_Callee()
        {
            var spans = Capture(Before);
            var host = Pod.HostOf("api");

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
            entry.GetTagItem("server.address").Should().Be(Pod.HostOf("web"));
        }

        [Test]
        public void Every_Service_Call_Has_Exactly_One_Answer_On_The_Same_Address()
        {
            // The inference trap from the {service}.internal attempt: a dialled host
            // nothing answers on is a peer nobody has seen. Every successful call's
            // host must be answered by exactly one SERVER span, its child.
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
            before.Deps.Should().Contain(Pod.HostOf("stock")).And.Contain("orders-postgres").And.Contain("orders-created");
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

            var call = spans.Single(s => Equals(s.GetTagItem("server.address"), Pod.HostOf("api")) && s.Kind == ActivityKind.Client);
            call.Status.Should().Be(ActivityStatusCode.Error);
            call.GetTagItem("http.response.status_code").Should().Be(500);
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
            var hosts = new[] { "api", "Api", "API", "a_pi" }.Select(Pod.HostOf).ToList();

            hosts.Should().OnlyHaveUniqueItems();
            hosts.Should().OnlyContain(h => System.Text.RegularExpressions.Regex.IsMatch(
                h, @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.default\.svc\.cluster\.local$"));
            Pod.HostOf("Api").Should().Be("api-qrx24.default.svc.cluster.local", "deterministic, so a shared link names the same host everywhere");
            Pod.HostOf("").Should().MatchRegex(@"^svc-[a-z0-9]{5}\.default\.svc\.cluster\.local$", "an empty id still gets a valid host");
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
                .Should().BeEquivalentTo(new object[] { Pod.HostOf("w1"), Pod.HostOf("w2") });
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
        public void A_Chain_Past_The_Depth_Limit_Stops_At_A_Whole_Call()
        {
            // 40 services in a line, no cycle: the walk stops at depth 32. The last
            // call is not emitted at all, rather than as a CLIENT span nothing
            // answers, and the hops are exactly the calls that were.
            var lines = new List<string> { "flowchart LR" };
            for (var i = 1; i < 40; i++) lines.Add($"  s{i}[S{i}] --> s{i + 1}[S{i + 1}]");

            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);
            using var pool = new PodTracerPool(target: null);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(string.Join("\n", lines)), 1, "test");
            var spans = captured.Where(a => a.TraceId.ToString() == result.TraceId).ToList();

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

                var captured = new List<Activity>();
                using var listener = new ActivityListener
                {
                    ShouldListenTo = _ => true,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = captured.Add,
                };
                ActivitySource.AddActivityListener(listener);
                using var pool = new PodTracerPool(target: null);
                var result = new TopologyRunner(pool).Run(MermaidParser.Parse(string.Join("\n", lines)), 1, "test");
                var spans = captured.Where(a => a.TraceId.ToString() == result.TraceId).ToList();

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
            var server = Capture("flowchart LR\n  web[Storefront] --> order_api[Orders API]")
                .Single(s => s.Kind == ActivityKind.Server && ServiceOf(s) == "orders-api");

            server.GetTagItem("http.route").Should().Be($"/{Pod.HostOf("order_api").Split('.')[0]}");
        }
    }
}
