using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Topology
{
    [TestFixture]
    public class MermaidParserTests
    {
        [Test]
        public void Shapes_Carry_Their_Semantics()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  api[Orders API] --> db[(Postgres)]
  api --> q[[Job Queue]]
  api --> cache((Redis))
  api --> ext{{Stripe}}");

            graph.ById("api")!.Kind.Should().Be(PodKind.Service);
            graph.ById("db")!.Kind.Should().Be(PodKind.Datastore);
            graph.ById("q")!.Kind.Should().Be(PodKind.Queue);
            graph.ById("cache")!.Kind.Should().Be(PodKind.Cache);
            graph.ById("ext")!.Kind.Should().Be(PodKind.External);
        }

        [Test]
        public void ServiceName_Comes_From_The_Label_Not_The_Id()
        {
            // "gw" in a service list is useless to somebody learning to read one.
            var graph = MermaidParser.Parse("flowchart LR\n  gw[API Gateway] --> a[Auth]");

            graph.ById("gw")!.ServiceName.Should().Be("api-gateway");
        }

        [Test]
        public void Replica_Suffix_Is_Read_From_The_Label()
        {
            var graph = MermaidParser.Parse("flowchart LR\n  q[[Queue]] --> worker[Worker x5]");

            var worker = graph.ById("worker")!;
            worker.Replicas.Should().Be(5);
            worker.Label.Should().Be("Worker", "the suffix is configuration, not part of the name");
            worker.ServiceName.Should().Be("worker");
            worker.PinnedInstance.Should().BeNull();
        }

        [Test]
        public void Instance_Suffix_Pins_One_Replica_Of_A_Shared_Service()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  q[[Queue]] --> w1[Worker #1]
  q --> w2[Worker #2]");

            // Same service, two pods. That is the whole point of the suffix.
            graph.ById("w1")!.ServiceName.Should().Be("worker");
            graph.ById("w2")!.ServiceName.Should().Be("worker");
            graph.ById("w1")!.PinnedInstance.Should().Be(1);
            graph.ById("w2")!.PinnedInstance.Should().Be(2);
        }

        [Test]
        public void Bare_Broken_Label_Fails_Every_Instance()
        {
            var graph = MermaidParser.Parse("flowchart LR\n  a[A] -->|broken| b[B]");

            var call = graph.Calls.Single();
            call.Broken.Should().BeTrue();
            call.BrokenInstances.Should().BeEmpty();
            call.FailsFor(1).Should().BeTrue();
            call.FailsFor(9).Should().BeTrue();
        }

        [Test]
        public void Broken_On_Instance_Fails_Only_That_Instance()
        {
            var graph = MermaidParser.Parse("flowchart LR\n  worker[Worker x5] -->|broken on #3| rabbit[[RabbitMQ]]");

            var call = graph.Calls.Single();
            call.FailsFor(3).Should().BeTrue();
            call.FailsFor(1).Should().BeFalse();
            call.FailsFor(5).Should().BeFalse();
        }

        [Test]
        public void Broken_On_Several_Instances_Is_Comma_Separated()
        {
            var graph = MermaidParser.Parse("flowchart LR\n  w[Worker x5] -->|broken on #2,#4| r[[Rabbit]]");

            var call = graph.Calls.Single();
            call.BrokenInstances.Should().BeEquivalentTo(new[] { 2, 4 });
            call.FailsFor(2).Should().BeTrue();
            call.FailsFor(3).Should().BeFalse();
        }

        [Test]
        public void Reason_After_The_Colon_Becomes_The_Failure_Text()
        {
            // Without this, four of the thirteen inherited scenarios collapse into
            // one indistinguishable diagram.
            var graph = MermaidParser.Parse("flowchart LR\n  api[API] -->|broken: wrong table| db[(SQL Server)]");

            graph.Calls.Single().FailureReason.Should().Be("wrong table");
        }

        [Test]
        public void Reason_Combines_With_An_Instance_Selector()
        {
            var graph = MermaidParser.Parse("flowchart LR\n  w[Worker x5] -->|broken on #3: connection refused| r[[Rabbit]]");

            var call = graph.Calls.Single();
            call.FailureReason.Should().Be("connection refused");
            call.BrokenInstances.Should().BeEquivalentTo(new[] { 3 });
        }

        [Test]
        public void A_Downed_Pod_Fails_Every_Call_Into_It()
        {
            // Different lesson from a broken edge: a dead service fails for every
            // caller, which is a different trace shape.
            var graph = MermaidParser.Parse(@"
flowchart LR
  a[A] --> p[Payments]
  b[B] --> p
  classDef broken stroke:#f00
  class p broken");

            graph.Calls.Should().OnlyContain(c => c.Broken);
            graph.Calls.Should().OnlyContain(c => c.FailureReason == "pod down");
        }

        [Test]
        public void Unknown_Lines_Become_Notes_And_Never_Throw()
        {
            // A diagram somebody drew years ago for a design doc has to run. That is
            // the property the whole paste box rests on.
            var graph = MermaidParser.Parse(@"
flowchart LR
  subgraph cluster
  a[A] --> b[B]
  end
  style a fill:#f9f");

            graph.Calls.Should().HaveCount(1);
            graph.Notes.Should().NotBeEmpty();
        }

        [Test]
        public void Entry_Point_Is_The_Pod_Nothing_Calls()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  gw[Gateway] --> orders[Orders]
  orders --> db[(DB)]");

            graph.Entry!.Id.Should().Be("gw");
        }

        [Test]
        public void Comments_Directives_And_ClassDef_Are_Ignored_Silently()
        {
            var graph = MermaidParser.Parse(@"
%% this is a comment
flowchart LR
  classDef broken stroke:#f00
  a[A] --> b[B]");

            graph.Pods.Should().HaveCount(2);
            graph.Notes.Should().BeEmpty("directives are expected, not surprises");
        }

        [Test]
        public void Empty_Diagram_Produces_An_Empty_Graph_Rather_Than_An_Error()
        {
            var graph = MermaidParser.Parse(string.Empty);

            graph.Pods.Should().BeEmpty();
            graph.Calls.Should().BeEmpty();
            graph.Entry.Should().BeNull();
        }
    }
}
