using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// Pods are named the way a Deployment names them, {service}-{templateHash}-{suffix},
    /// and a rev marker is how a diagram says "every pod of this service was replaced".
    /// Scaling keeps pod names; a redeploy changes all of them, and a rollout has a
    /// window where the old and the new set both emit.
    /// </summary>
    [TestFixture]
    public class RevisionTests
    {
        private static readonly Regex PodName =
            new("^(?<service>[a-z0-9-]+)-(?<hash>[bcdfghjklmnpqrstvwxz2456789]{10})-(?<suffix>[bcdfghjklmnpqrstvwxz2456789]{5})$");

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

        private static Pod Api(string label) => MermaidParser.Parse(Orders(label)).ById("api")!;

        private static string Hash(string podName) => PodName.Match(podName).Groups["hash"].Value;

        [TestCase("Orders API")]
        [TestCase("Orders API x3")]
        [TestCase("Orders API x3 rev2")]
        [TestCase("Orders API #2 rev7")]
        public void Every_Pod_Name_Looks_Like_A_Deployment_Pod(string label)
        {
            var api = Api(label);

            foreach (var name in api.InstanceIds)
            {
                var match = PodName.Match(name);
                match.Success.Should().BeTrue($"{name} should be service-hash10-suffix5");
                match.Groups["service"].Value.Should().Be("orders-api");
            }
        }

        [Test]
        public void The_Alphabet_Is_The_Kubernetes_Safe_Alphabet()
        {
            Pod.SafeAlphabet.Should().Be("bcdfghjklmnpqrstvwxz2456789");

            var generated = string.Concat(Enumerable.Range(1, 200)
                .Select(n => Pod.InstanceIdOf("svc", n, n % 4))
                .Select(id => id["svc-".Length..].Replace("-", string.Empty)));
            generated.ToCharArray().Should().OnlyContain(c => Pod.SafeAlphabet.Contains(c));
        }

        [Test]
        public void Names_Are_Deterministic()
        {
            // A shared link has to name the same pods on any machine, so no GetHashCode.
            Pod.InstanceIdOf("orders-api", 1, 2).Should().Be(Pod.InstanceIdOf("orders-api", 1, 2));

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
        public void Pods_Of_One_Service_At_One_Revision_Share_A_Template_Hash_And_Differ_By_Suffix()
        {
            var names = Api("Orders API x5 rev2").InstanceIds;

            names.Select(Hash).Distinct().Should().ContainSingle();
            names.Should().OnlyHaveUniqueItems();
        }

        [TestCase("Orders API x3 rev2", 3)]
        [TestCase("Orders API rev2 x3", 3)]
        [TestCase("Orders API rev2", 1)]
        public void The_Revision_Suffix_Is_Configuration_Not_Name(string label, int replicas)
        {
            var api = Api(label);

            api.Revision.Should().Be(2);
            api.Replicas.Should().Be(replicas);
            api.Label.Should().Be("Orders API");
            api.ServiceName.Should().Be("orders-api", "a redeploy is the same service");
        }

        [Test]
        public void A_Pinned_Instance_Is_A_Position_Not_Part_Of_The_Name()
        {
            var api = Api("Orders API #2 rev3");

            api.PinnedInstance.Should().Be(2);
            api.InstanceIds.Should().Equal(Pod.InstanceIdOf("orders-api", 2, 3));
        }

        [TestCase("Orders API")]
        [TestCase("Orders API x3")]
        [TestCase("Orders API v2")]
        [TestCase("Rev2")]
        [TestCase("Orders Rev2")]
        [TestCase("Abbrev2")]
        public void Labels_Without_The_Marker_Are_Revision_Zero(string label)
        {
            var api = Api(label);

            api.Revision.Should().BeNull();
            api.InstanceId(1).Should().Be(Pod.InstanceIdOf(api.ServiceName, 1, 0));
        }

        [Test]
        public void Scaling_Up_Keeps_The_Existing_Names_And_Adds_New_Ones()
        {
            var three = Api("Orders API x3 rev1").InstanceIds;
            var five = Api("Orders API x5 rev1").InstanceIds;

            five.Take(3).Should().Equal(three);
            five.Skip(3).Should().NotIntersectWith(three);
        }

        [Test]
        public void Bumping_The_Revision_Changes_Every_Name_And_The_Template_Hash()
        {
            var rev1 = Api("Orders API x3 rev1").InstanceIds;
            var rev2 = Api("Orders API x3 rev2").InstanceIds;

            rev1.Should().NotIntersectWith(rev2, "a redeploy replaces every pod");
            Hash(rev1[0]).Should().NotBe(Hash(rev2[0]));

            Enumerable.Range(1, 3).SelectMany(r => Fire(Orders("Orders API x3 rev2"), r).ServedBy)
                .Should().Contain(rev2);
        }

        [Test]
        public void Bumping_One_Service_Leaves_Every_Other_Service_Alone()
        {
            var before = MermaidParser.Parse("flowchart LR\n  gw[Gateway x2] --> api[Orders API x3 rev1]");
            var after = MermaidParser.Parse("flowchart LR\n  gw[Gateway x2] --> api[Orders API x3 rev2]");

            after.ById("gw")!.InstanceIds.Should().Equal(before.ById("gw")!.InstanceIds);
            after.ById("api")!.InstanceIds.Should().NotIntersectWith(before.ById("api")!.InstanceIds);
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
            var old = graph.ById("old")!;
            var @new = graph.ById("new")!;
            old.ServiceName.Should().Be(@new.ServiceName);
            Hash(old.InstanceId(1)).Should().NotBe(Hash(@new.InstanceId(1)));

            Fire(overlap, 1).ServedBy.Should().Contain(old.InstanceId(1)).And.Contain(@new.InstanceId(1));
        }

        [Test]
        public void The_Scope_Name_Is_The_Pod_Name()
        {
            using var activity = _pool.For("orders-api", 1, revision: 2).StartActivity("orders-api handle");

            activity.Should().NotBeNull();
            activity!.Source.Name.Should().Be(Pod.InstanceIdOf("orders-api", 1, 2));
            _pool.For("orders-api", 1).Name.Should().Be(Pod.InstanceIdOf("orders-api", 1, 0));
        }

        [Test]
        public void A_Taken_Suffix_Is_Redrawn_And_Earlier_Positions_Never_Move()
        {
            // Every first draw collides; only the attempt counter tells them apart.
            static string Candidate(int position, int attempt) => attempt == 0 ? "bbbbb" : $"r{position}a{attempt}";

            var three = Pod.AssignSuffixes(3, Candidate);
            three.Should().Equal("bbbbb", "r2a1", "r3a1");
            three.Should().OnlyHaveUniqueItems("two pods must never share a name");

            Pod.AssignSuffixes(5, Candidate).Take(3).Should().Equal(three, "scaling up keeps every existing name");
        }

        [Test]
        public void A_Redraw_Skips_Every_Name_A_Lower_Position_Holds()
        {
            // Position 3's first two draws are both already taken by positions 1 and 2.
            var draws = new Dictionary<(int, int), string>
            {
                [(1, 0)] = "aaaaa",
                [(2, 0)] = "ccccc",
                [(3, 0)] = "aaaaa",
                [(3, 1)] = "ccccc",
                [(3, 2)] = "ddddd",
            };

            Pod.AssignSuffixes(3, (n, attempt) => draws[(n, attempt)]).Should().Equal("aaaaa", "ccccc", "ddddd");
        }

        [Test]
        public void Many_Replicas_Still_Get_Distinct_Names()
        {
            var names = Api($"Orders API x{Pod.UniqueNamePositions}").InstanceIds;

            names.Should().HaveCount(Pod.UniqueNamePositions).And.OnlyHaveUniqueItems();
            names[^1].Should().Be(Pod.InstanceIdOf("orders-api", Pod.UniqueNamePositions, null));
        }

        [Test]
        public void A_Huge_Replica_Count_Costs_A_Bounded_Amount()
        {
            // Naming is O(position); a pasted x2000000000 must not make a run or a parse unbounded.
            var api = Api("Orders API x2000000000");

            api.InstanceIds.Should().HaveCount(Pod.UniqueNamePositions);
            PodName.IsMatch(api.InstanceId(1999999999)).Should().BeTrue();
            PodName.IsMatch(Api("Orders API #0").InstanceIds.Single()).Should().BeTrue();
        }

        [Test]
        public void Similar_Service_Names_Get_Separate_Pods()
        {
            // "x" and "x-r1" were the colliding pair under the old format; any two
            // services must stay two pods however their names line up.
            var first = _pool.For("x", 2, revision: 1);
            var second = _pool.For("x-r1", 2);

            second.Should().NotBeSameAs(first);
            second.Name.Should().NotBe(first.Name);
            first.Name.Should().StartWith("x-");
            second.Name.Should().StartWith("x-r1-");
        }

        [TestCase("Orders API rev0", 0)]
        [TestCase("Orders API rev02", 2)]
        public void Rev0_Is_Allowed_And_Leading_Zeros_Normalize(string label, int revision)
        {
            var api = Api(label);

            api.Revision.Should().Be(revision);
            api.InstanceId(1).Should().Be(Pod.InstanceIdOf("orders-api", 1, revision));
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

        [Test]
        public void The_Names_Are_Pinned_So_Old_Links_Keep_Naming_The_Same_Pods()
        {
            // The documented example. If this changes, every shared link names new pods.
            Api("Orders API x3 rev2").InstanceIds.Should().Equal(
                "orders-api-9fgkfp96z4-bh4ks",
                "orders-api-9fgkfp96z4-kkkwx",
                "orders-api-9fgkfp96z4-mmf6q");
        }
    }
}
