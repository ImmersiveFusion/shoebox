using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// The arrows a run could never have crossed, as opposed to the ones it turned down.
    ///
    /// Declining is something the walk does, so it only ever happens to an edge the walk
    /// reached. An edge leaving a pod that nothing calls is never offered and never refused,
    /// and it used to fall out of <see cref="RunResult.NotTaken"/> entirely -- the largest
    /// difference between a diagram and its run was the one difference the field could not
    /// report.
    ///
    /// Measured 2026-09-23. A model drew the diagram below from a screenshot and left the
    /// pub/sub topic with no publisher: 15 pods, 20 edges, 12 crossed, 1 reported untaken,
    /// and 7 accounted for nowhere. It parsed clean, ran clean and shared clean, and the
    /// picture behind the share link showed all 20 arrows alike.
    /// </summary>
    [TestFixture]
    public class UnreachableEdgeTests
    {
        private PodTracerPool _pool = null!;
        private TopologyRunner _runner = null!;

        [SetUp]
        public void SetUp()
        {
            _pool = new PodTracerPool(target: null);
            _runner = new TopologyRunner(_pool);
        }

        [TearDown]
        public void TearDown() => _pool.Dispose();

        /// <summary>
        /// Verbatim, hyphenated ids and all. Nothing publishes to service-bus and nothing
        /// writes through blob-binding, so both — and everything only they call — are drawn
        /// and never touched.
        /// </summary>
        private const string TopicWithNoPublisher = @"
flowchart TD
    user[User] --> envoy[envoy]
    envoy --> ui[UI]
    ui --> envoy
    envoy --> virtual-customer[Virtual customer]
    virtual-customer --> order-service[Order service]
    envoy --> accounting-service[Accounting service]
    accounting-service --> order-service
    accounting-service --> ui
    service-bus[[Publish-subscribe topic]] --> order-service
    service-bus[[Publish-subscribe topic]] --> accounting-service
    service-bus[[Publish-subscribe topic]] --> receiptservice[Receiptservice]
    service-bus[[Publish-subscribe topic]] --> loyaltyservice[Loyaltyservice]
    service-bus[[Publish-subscribe topic]] --> makeline-service[Makeline service]
    loyaltyservice --> cosmos-db[(Azure Cosmos DB)]
    accounting-service --> sql-db[(Azure SQL Database)]
    makeline-service --> redis[(Azure Managed Redis)]
    blob-binding[[Azure Blob Storage binding]] --> sql-db
    envoy --> virtual-worker[Virtual worker]
    envoy --> makeline-service
    virtual-worker --> makeline-service";

        /// <summary>Every arrow crossed at least once, so there is nothing to report.</summary>
        private const string FullyReachable = @"
flowchart TD
    u[User] --> a[Alpha]
    a --> b[Bravo]
    a --> c[Charlie]";

        private RunResult Fire(string diagram) =>
            _runner.Run(MermaidParser.Parse(diagram), runIndex: 1, shoeboxId: "test");

        [Test]
        public void Every_Edge_Is_Either_Crossed_Or_Reported()
        {
            // The invariant the field is for. A renderer greys out what NotTaken names and draws
            // the rest as travelled, so an edge in neither set is drawn as a lie.
            var result = Fire(TopicWithNoPublisher);

            var crossed = result.Hops.Select(h => $"{h.From}->{h.To}").ToHashSet();
            var reported = result.NotTaken.Select(n => $"{n.From}->{n.To}").ToHashSet();
            var drawn = MermaidParser.Parse(TopicWithNoPublisher).Calls
                .Select(c => $"{c.FromId}->{c.ToId}").ToHashSet();

            drawn.Should().OnlyContain(e => crossed.Contains(e) || reported.Contains(e));
        }

        [Test]
        public void An_Edge_Leaving_A_Pod_Nothing_Calls_Comes_Back_As_Data()
        {
            var result = Fire(TopicWithNoPublisher);

            var reported = result.NotTaken.Where(n => n.From == "service-bus").ToList();

            reported.Should().HaveCount(5, "the topic subscribes five services and publishes to none");
            reported.Should().OnlyContain(n => n.Reason.Contains("service-bus"),
                "the pod that never got called is the thing to fix, so the reason names it");
        }

        [Test]
        public void A_Reported_Edge_Is_Never_Also_A_Crossed_One()
        {
            var result = Fire(TopicWithNoPublisher);

            var crossed = result.Hops.Select(h => $"{h.From}->{h.To}").ToHashSet();
            result.NotTaken.Should().OnlyContain(n => !crossed.Contains($"{n.From}->{n.To}"));
        }

        [Test]
        public void An_Unreachable_Edge_Reads_Differently_From_A_Refused_One()
        {
            // b -> a is refused because the request is already at a; service-bus -> order-service
            // was never put to the walk at all. Both are untaken and a reader has to be able to
            // tell them apart, because only one of them is a mistake in the picture.
            var refused = _runner.Run(MermaidParser.Parse(@"
flowchart TD
    u[User] --> a[Alpha]
    a --> b[Bravo]
    b --> a"), runIndex: 1, shoeboxId: "test").NotTaken.Single();

            var unreached = Fire(TopicWithNoPublisher).NotTaken.First(n => n.From == "service-bus");

            refused.Reason.Should().NotBe(unreached.Reason);
            unreached.Reason.Should().Contain("never reached");
        }

        [Test]
        public void A_Diagram_Nothing_Can_Start_Reports_Every_Edge()
        {
            // No entry point means nothing ran, so the difference between the diagram and the run
            // is the whole diagram. An empty list here said the opposite.
            var everyPodIsCalled = @"
flowchart TD
    a[Alpha] --> b[Bravo]
    b --> a";

            var result = Fire(everyPodIsCalled);

            result.Hops.Should().BeEmpty();
            result.NotTaken.Should().HaveCount(
                MermaidParser.Parse(everyPodIsCalled).Calls.Count);
        }

        [Test]
        public void A_Diagram_A_Run_Walks_Completely_Still_Reports_Nothing()
        {
            // The guard on the other side: widening this must not start greying out arrows the
            // request did cross.
            Fire(FullyReachable).NotTaken.Should().BeEmpty();
        }
    }
}
