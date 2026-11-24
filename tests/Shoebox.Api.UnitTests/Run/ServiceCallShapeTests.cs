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
            static List<string> Shape(List<Activity> spans, string renamed)
            {
                var order = spans.OrderBy(s => s.StartTimeUtc).ThenBy(s => s.Kind).ThenBy(s => s.DisplayName, StringComparer.Ordinal).ToList();
                var index = order.Select((s, i) => (s.SpanId, i)).ToDictionary(p => p.SpanId, p => p.i);
                return order.Select(s =>
                {
                    var service = ServiceOf(s) == renamed ? "RENAMED" : ServiceOf(s);
                    var parent = s.ParentSpanId == default ? -1 : index[s.ParentSpanId];
                    var tags = string.Join(";", s.TagObjects
                        .Where(t => t.Key != "service.instance.id")
                        .OrderBy(t => t.Key, StringComparer.Ordinal)
                        .Select(t => $"{t.Key}={t.Value}"));
                    return $"{service}|{s.Kind}|{s.DisplayName.Replace(renamed, "RENAMED")}|{parent}|{s.Status}|{tags}";
                }).ToList();
            }

            Shape(Capture(After), "orders-service").Should().Equal(Shape(Capture(Before), "orders-api"));
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
            Pod.HostOf("Api").Should().Be(Pod.HostOf("Api"), "deterministic, so a shared link names the same host");
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
        public void A_Rev_Bump_Is_A_New_Version_On_The_Same_Address()
        {
            var before = Capture("flowchart LR\n  web[Storefront] --> api[Orders API x2]");
            var after = Capture("flowchart LR\n  web[Storefront] --> api[Orders API x2 rev1]");

            Pod.VersionOf(1).Should().NotBe(Pod.VersionOf(null));
            after.Single(s => ServiceOf(s) == "orders-api").GetTagItem("server.address")
                .Should().Be(before.Single(s => ServiceOf(s) == "orders-api").GetTagItem("server.address"));
        }
    }
}
