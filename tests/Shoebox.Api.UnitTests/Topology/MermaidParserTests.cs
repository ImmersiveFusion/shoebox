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
        // ── Arrow forms a model actually writes ──────────────────────────────
        //
        // These all used to be dropped into notes while the run returned 200, which
        // is how a green trace came back for a system nobody drew.

        [Test]
        public void A_Chain_Is_Every_Edge_In_It()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  apg[Gateway] --> lb[Balancer] --> aks[Cluster]");

            graph.Calls.Should().HaveCount(2);
            graph.Calls[0].FromId.Should().Be("apg");
            graph.Calls[0].ToId.Should().Be("lb");
            graph.Calls[1].FromId.Should().Be("lb");
            graph.Calls[1].ToId.Should().Be("aks");
            graph.Notes.Should().BeEmpty();
        }

        [Test]
        public void Dotted_Arrows_Are_Calls_And_Their_Text_Is_A_Label()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  fleet[Fleet] -. manages .-> aks[Cluster]
  a[A] -.-> b[B]");

            graph.Calls.Should().HaveCount(2);
            graph.Calls.Should().OnlyContain(c => !c.Broken && !c.Phantom, "'manages' is not one of the keywords");
            graph.Notes.Should().BeEmpty();
        }

        [Test]
        public void Thick_And_Inline_Label_Arrows_Are_Read()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  a[A] ==> b[B]
  c[C] -- calls --> d[D]
  e[E] == sends ==> f[F]");

            graph.Calls.Should().HaveCount(3);
            graph.Notes.Should().BeEmpty();
        }

        [Test]
        public void Keywords_Survive_A_Dotted_Arrow()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  q[[Queue]] -. phantom .-> w[Worker]");

            graph.Calls.Single().Phantom.Should().BeTrue();
        }

        [Test]
        public void An_Undirected_Link_Is_Read_Left_To_Right_And_Says_So()
        {
            // Half the time the call goes the other way -- a service calls its key
            // vault, not the reverse -- so the guess has to be visible.
            var graph = MermaidParser.Parse(@"
flowchart LR
  kv[Key Vault] --- aks[Cluster]");

            var call = graph.Calls.Single();
            call.FromId.Should().Be("kv");
            call.ToId.Should().Be("aks");
            graph.Notes.Should().ContainSingle().Which.Should().Contain("undirected");
        }

        [Test]
        public void Subgraphs_Are_Skipped_Without_Being_Called_Unreadable()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  subgraph RA[""Region A""]
    direction LR
    aks[Cluster]
  end
  gw[Gateway] --> aks");

            graph.Pods.Should().HaveCount(2, "a subgraph is not a service");
            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.Notes.Should().ContainSingle().Which.Should().Contain("subgraphs ignored");
        }

        [Test]
        public void An_Edge_To_A_Subgraph_Is_Named_As_Such()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  subgraph HUBA[""Hub""]
    fw[Firewall]
  end
  bastion[Bastion] --> HUBA");

            graph.Notes.Should().Contain(n => n.Contains("HUBA is a subgraph"));
        }

        [Test]
        public void Labels_Lose_Their_Quotes_And_Line_Breaks()
        {
            // A model writes ["Log Analytics\n(regional)"], and the escape used to
            // arrive literally: log-analytics-n-regional, with the n from the \n.
            var graph = MermaidParser.Parse(@"
flowchart LR
  a[""Log Analytics\n(regional)""] --> b[""Sink""]");

            var pod = graph.ById("a")!;
            pod.Label.Should().Be("Log Analytics (regional)");
            pod.ServiceName.Should().Be("log-analytics-regional");
            graph.ById("b")!.Label.Should().Be("Sink");
        }

        [Test]
        public void A_Half_Readable_Line_Is_Refused_Whole()
        {
            // Admitting the readable half is how a diagram silently becomes a
            // different diagram.
            var graph = MermaidParser.Parse(@"
flowchart LR
  a[A] --> b[B] --> ");

            graph.Calls.Should().BeEmpty();
            graph.Notes.Should().ContainSingle().Which.Should().Contain("not understood");
        }

        /// <remarks>
        /// The diagram a model actually writes. Ids used to be [A-Za-z0-9_]+, so every
        /// line carrying a hyphenated id was dropped as "not understood" while the rest
        /// parsed normally -- the partial read this parser exists to prevent. A 15-service
        /// paste came back as the 3 services whose lines happened to be hyphen-free, with
        /// no error, and the caller rewrote and retried against a rate limit.
        ///
        /// The SPA has always accepted them: diagram-style.ts matches ids as
        /// [A-Za-z][\w-]*. Only the runner disagreed.
        /// </remarks>
        [Test]
        public void Hyphenated_Ids_Are_Read_Not_Dropped()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  user[User] --> envoy[Envoy]
  envoy --> virtual-customer[Virtual Customer]
  virtual-customer --> order-service[Order Service]
  order-service --> db-primary[(Postgres)]");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.Pods.Select(p => p.Id).Should()
                .Equal("user", "envoy", "virtual-customer", "order-service", "db-primary");
            graph.ById("db-primary")!.Kind.Should().Be(PodKind.Datastore);
        }

        [Test]
        public void A_Hyphenated_Id_Survives_Every_Arrow_Form()
        {
            // The undirected case is the one worth pinning: a-b---c-d has to split as
            // two ids and one link, not four dashes read as an id.
            var graph = MermaidParser.Parse(@"
flowchart LR
  order-service -. manages .-> db-primary[(Store)]
  order-service -- publishes --> job-queue[[Queue]]
  gw-1 ==> order-service
  cache-a---cache-b");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.Calls.Select(c => $"{c.FromId}->{c.ToId}").Should().Contain(new[]
            {
                "order-service->db-primary",
                "order-service->job-queue",
                "gw-1->order-service",
                "cache-a->cache-b",
            });
        }

        [Test]
        public void A_Hyphenated_Id_Can_Be_Marked_Broken()
        {
            var graph = MermaidParser.Parse(@"
flowchart LR
  api[API] --> order-service[Order Service]
  class order-service broken");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.Calls.Should().ContainSingle()
                .Which.Broken.Should().BeTrue("a downed pod fails every call into it");
        }
    }
}
