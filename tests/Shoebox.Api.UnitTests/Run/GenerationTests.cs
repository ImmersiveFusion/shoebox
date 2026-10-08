using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// A generation marker is how a diagram says "every pod of this service was
    /// replaced". Scaling keeps replica ids; a redeploy must change all of them, and a
    /// rollout has a window where the old and the new set both emit.
    /// </summary>
    [TestFixture]
    public class GenerationTests
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

        [TestCase("Orders API x3 gen2", 3)]
        [TestCase("Orders API gen2 x3", 3)]
        [TestCase("Orders API gen2", 1)]
        public void The_Generation_Suffix_Is_Configuration_Not_Name(string label, int replicas)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Generation.Should().Be(2);
            api.Replicas.Should().Be(replicas);
            api.Label.Should().Be("Orders API");
            api.ServiceName.Should().Be("orders-api", "a redeploy is the same service");
        }

        [Test]
        public void A_Generation_Combines_With_A_Pinned_Instance()
        {
            var api = MermaidParser.Parse(Orders("Orders API #2 gen3")).ById("api")!;

            api.PinnedInstance.Should().Be(2);
            api.Generation.Should().Be(3);
            api.InstanceId(2).Should().Be("orders-api-g3-2");
        }

        [TestCase("Orders API")]
        [TestCase("Orders API x3")]
        [TestCase("Orders API v2")]
        [TestCase("Report Gen2")]
        [TestCase("Codegen2")]
        public void Labels_Without_The_Marker_Keep_Their_Name_And_Ids(string label)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Generation.Should().BeNull();
            api.InstanceId(1).Should().Be($"{api.ServiceName}-1");
        }

        [Test]
        public void Without_A_Generation_Instance_Ids_Are_Unchanged()
        {
            // Backward compatible: every link written before generations replays as it did.
            for (var run = 1; run <= 3; run++)
            {
                Fire(Orders("Orders API x3"), run).ServedBy.Should().Contain($"orders-api-{run}");
            }
        }

        [Test]
        public void Bumping_The_Generation_Replaces_Every_Instance_Id()
        {
            var gen1 = Enumerable.Range(1, 3).SelectMany(r => Fire(Orders("Orders API x3 gen1"), r).ServedBy)
                .Where(s => s.StartsWith("orders-api", StringComparison.Ordinal)).ToHashSet();
            var gen2 = Enumerable.Range(1, 3).SelectMany(r => Fire(Orders("Orders API x3 gen2"), r).ServedBy)
                .Where(s => s.StartsWith("orders-api", StringComparison.Ordinal)).ToHashSet();

            gen1.Should().BeEquivalentTo(new[] { "orders-api-g1-1", "orders-api-g1-2", "orders-api-g1-3" });
            gen2.Should().BeEquivalentTo(new[] { "orders-api-g2-1", "orders-api-g2-2", "orders-api-g2-3" });
            gen1.Should().NotIntersectWith(gen2, "a redeploy replaces every pod");
        }

        [Test]
        public void Scaling_Within_A_Generation_Keeps_The_Surviving_Ids()
        {
            Fire(Orders("Orders API x2 gen1"), 2).ServedBy.Should().Contain("orders-api-g1-2");
            Fire(Orders("Orders API x4 gen1"), 2).ServedBy.Should().Contain("orders-api-g1-2");
        }

        [Test]
        public void Two_Generations_Drawn_Together_Both_Emit_As_One_Service()
        {
            // The overlap window of a rollout: old and new pods live at once, one service.
            const string overlap = @"
flowchart LR
  gw[Gateway] --> old[Orders API x2 gen1]
  gw --> new[Orders API x2 gen2]
  old --> db[(Postgres)]
  new --> db";

            var graph = MermaidParser.Parse(overlap);
            graph.ById("old")!.ServiceName.Should().Be(graph.ById("new")!.ServiceName);

            var served = Fire(overlap, 1).ServedBy;
            served.Should().Contain("orders-api-g1-1").And.Contain("orders-api-g2-1");
        }

        [Test]
        public void The_Same_Diagram_And_Run_Replay_Identically()
        {
            const string diagram = @"
flowchart LR
  gw[Gateway] --> old[Orders API x2 gen1]
  gw --> new[Orders API x3 gen2]";

            for (var run = 1; run <= 6; run++)
            {
                Fire(diagram, run).ServedBy.Should().Equal(Fire(diagram, run).ServedBy);
            }
        }

        [Test]
        public void The_Span_Resource_Carries_The_Generationed_Instance_Id()
        {
            using var activity = _pool.For("orders-api", 1, generation: 2).StartActivity("orders-api handle");

            activity.Should().NotBeNull();
            activity!.Source.Name.Should().Be("orders-api/orders-api-g2-1");
        }

        [Test]
        public void A_Pod_Without_A_Generation_Keeps_Its_Old_Scope_Name()
        {
            // The source name is the OTLP instrumentation scope name, so existing
            // diagrams must export exactly what they did before generations existed.
            _pool.For("orders-api", 1).Name.Should().Be("orders-api-1");
        }

        [Test]
        public void Two_Services_That_Share_An_Instance_Id_Get_Separate_Pods()
        {
            // Service "x" gen1 pod 2 and a service labelled "X G1" pod 2 are both x-g1-2.
            var x = MermaidParser.Parse(@"
flowchart LR
  a[X gen1] --> b[X G1]");
            x.ById("a")!.InstanceId(2).Should().Be(x.ById("b")!.InstanceId(2));

            var first = _pool.For("x", 2, generation: 1);
            var second = _pool.For("x-g1", 2);

            second.Should().NotBeSameAs(first, "each pod exports under its own service.name");
            first.Name.Should().Be("x/x-g1-2");
            second.Name.Should().Be("x-g1-2", "a pod with no generation keeps its old name");
        }

        [TestCase("Orders API gen0", 0, "orders-api-g0-1")]
        [TestCase("Orders API gen02", 2, "orders-api-g2-1")]
        public void Gen0_Is_Allowed_And_Leading_Zeros_Normalize(string label, int generation, string id)
        {
            var api = MermaidParser.Parse(Orders(label)).ById("api")!;

            api.Generation.Should().Be(generation);
            api.InstanceId(1).Should().Be(id);
        }

        [TestCase("Orders gen1 gen2")]
        [TestCase("Orders gen1 x3 gen2")]
        [TestCase("Orders gen1 gen2 x3")]
        public void A_Second_Marker_Stays_In_The_Name_And_Says_So(string label)
        {
            var graph = MermaidParser.Parse(Orders(label));
            var api = graph.ById("api")!;

            api.Generation.Should().Be(2);
            api.ServiceName.Should().Be("orders-gen1");
            graph.Notes.Should().ContainSingle(n => n.Contains("more than one gen marker", StringComparison.Ordinal));
        }

        [Test]
        public void One_Marker_Leaves_No_Note()
        {
            MermaidParser.Parse(Orders("Orders API x3 gen2")).Notes.Should().BeEmpty();
        }
    }
}
