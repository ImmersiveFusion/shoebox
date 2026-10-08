using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// A rev marker is how a diagram says "every pod of this service was
    /// replaced". Scaling keeps replica ids; a redeploy must change all of them, and a
    /// rollout has a window where the old and the new set both emit.
    /// </summary>
    [TestFixture]
    public class RevisionTests
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

        private RunResult Fire(string diagram, int runIndex) =>
            _runner.Run(MermaidParser.Parse(diagram), runIndex, shoeboxId: "test");

        private static string Orders(string label) => $@"
flowchart LR
  gw[Gateway] --> api[{label}]
  api --> db[(Postgres)]";

        [TestCase("Orders API x3 rev2", 3)]
        [TestCase("Orders API rev2 x3", 3)]
        [TestCase("Orders API rev2", 1)]
        public void The_Revision_Suffix_Is_Configuration_Not_Name(string label, int replicas)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Revision.Should().Be(2);
            api.Replicas.Should().Be(replicas);
            api.Label.Should().Be("Orders API");
            api.ServiceName.Should().Be("orders-api", "a redeploy is the same service");
        }

        [Test]
        public void A_Revision_Combines_With_A_Pinned_Instance()
        {
            var api = MermaidParser.Parse(Orders("Orders API #2 rev3")).ById("api")!;

            api.PinnedInstance.Should().Be(2);
            api.Revision.Should().Be(3);
            api.InstanceId(2).Should().Be("orders-api-r3-2");
        }

        [TestCase("Orders API")]
        [TestCase("Orders API x3")]
        [TestCase("Orders API v2")]
        [TestCase("Rev2")]
        [TestCase("Orders Rev2")]
        [TestCase("Abbrev2")]
        public void Labels_Without_The_Marker_Keep_Their_Name_And_Ids(string label)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Revision.Should().BeNull();
            api.InstanceId(1).Should().Be($"{api.ServiceName}-1");
        }

        [Test]
        public void Without_A_Revision_Instance_Ids_Are_Unchanged()
        {
            // Backward compatible: every link written before revisions replays as it did.
            for (var run = 1; run <= 3; run++)
            {
                Fire(Orders("Orders API x3"), run).ServedBy.Should().Contain($"orders-api-{run}");
            }
        }

        [Test]
        public void Bumping_The_Revision_Replaces_Every_Instance_Id()
        {
            var rev1 = Enumerable.Range(1, 3).SelectMany(r => Fire(Orders("Orders API x3 rev1"), r).ServedBy)
                .Where(s => s.StartsWith("orders-api", StringComparison.Ordinal)).ToHashSet();
            var rev2 = Enumerable.Range(1, 3).SelectMany(r => Fire(Orders("Orders API x3 rev2"), r).ServedBy)
                .Where(s => s.StartsWith("orders-api", StringComparison.Ordinal)).ToHashSet();

            rev1.Should().BeEquivalentTo(new[] { "orders-api-r1-1", "orders-api-r1-2", "orders-api-r1-3" });
            rev2.Should().BeEquivalentTo(new[] { "orders-api-r2-1", "orders-api-r2-2", "orders-api-r2-3" });
            rev1.Should().NotIntersectWith(rev2, "a redeploy replaces every pod");
        }

        [Test]
        public void Scaling_Within_A_Revision_Keeps_The_Surviving_Ids()
        {
            Fire(Orders("Orders API x2 rev1"), 2).ServedBy.Should().Contain("orders-api-r1-2");
            Fire(Orders("Orders API x4 rev1"), 2).ServedBy.Should().Contain("orders-api-r1-2");
        }

        [Test]
        public void Two_Revisions_Drawn_Together_Both_Emit_As_One_Service()
        {
            // The overlap window of a rollout: old and new pods live at once, one service.
            const string overlap = @"
flowchart LR
  gw[Gateway] --> old[Orders API x2 rev1]
  gw --> new[Orders API x2 rev2]
  old --> db[(Postgres)]
  new --> db";

            var graph = MermaidParser.Parse(overlap);
            graph.ById("old")!.ServiceName.Should().Be(graph.ById("new")!.ServiceName);

            var served = Fire(overlap, 1).ServedBy;
            served.Should().Contain("orders-api-r1-1").And.Contain("orders-api-r2-1");
        }

        [Test]
        public void The_Same_Diagram_And_Run_Replay_Identically()
        {
            const string diagram = @"
flowchart LR
  gw[Gateway] --> old[Orders API x2 rev1]
  gw --> new[Orders API x3 rev2]";

            for (var run = 1; run <= 6; run++)
            {
                Fire(diagram, run).ServedBy.Should().Equal(Fire(diagram, run).ServedBy);
            }
        }

        [Test]
        public void The_Span_Resource_Carries_The_Revisioned_Instance_Id()
        {
            using var activity = _pool.For("orders-api", 1, revision: 2).StartActivity("orders-api handle");

            activity.Should().NotBeNull();
            activity!.Source.Name.Should().Be("orders-api/orders-api-r2-1");
        }

        [Test]
        public void A_Pod_Without_A_Revision_Keeps_Its_Old_Scope_Name()
        {
            // The source name is the OTLP instrumentation scope name, so existing
            // diagrams must export exactly what they did before revisions existed.
            _pool.For("orders-api", 1).Name.Should().Be("orders-api-1");
        }

        [Test]
        public void Two_Services_That_Share_An_Instance_Id_Get_Separate_Pods()
        {
            // Service "x" rev1 pod 2 and a service labelled "X R1" pod 2 are both x-r1-2.
            var x = MermaidParser.Parse(@"
flowchart LR
  a[X rev1] --> b[X R1]");
            x.ById("a")!.InstanceId(2).Should().Be(x.ById("b")!.InstanceId(2));

            var first = _pool.For("x", 2, revision: 1);
            var second = _pool.For("x-r1", 2);

            second.Should().NotBeSameAs(first, "each pod exports under its own service.name");
            first.Name.Should().Be("x/x-r1-2");
            second.Name.Should().Be("x-r1-2", "a pod with no revision keeps its old name");
        }

        [TestCase("Orders API rev0", 0, "orders-api-r0-1")]
        [TestCase("Orders API rev02", 2, "orders-api-r2-1")]
        public void Rev0_Is_Allowed_And_Leading_Zeros_Normalize(string label, int revision, string id)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Revision.Should().Be(revision);
            api.InstanceId(1).Should().Be(id);
        }

        [TestCase("Orders rev1 rev2")]
        [TestCase("Orders rev1 x3 rev2")]
        [TestCase("Orders rev1 rev2 x3")]
        public void A_Second_Marker_Stays_In_The_Name_And_Says_So(string label)
        {
            var graph = MermaidParser.Parse(Orders(label));
            var api = graph.ById("api")!;

            api.Revision.Should().Be(2);
            api.ServiceName.Should().Be("orders-rev1");
            graph.Notes.Should().ContainSingle(n => n.Contains("more than one rev marker", StringComparison.Ordinal));
        }

        [Test]
        public void One_Marker_Leaves_No_Note()
        {
            MermaidParser.Parse(Orders("Orders API x3 rev2")).Notes.Should().BeEmpty();
        }
    }
}
