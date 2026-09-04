using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// The diagram that broke it, and the bound that holds it.
    ///
    /// Read faithfully off a stock Azure reference architecture on 2026-09-03:
    /// every service that publishes to the pub/sub topic is also subscribed to
    /// it, which is how the picture is actually drawn. One POST /run produced
    /// 23,428 spans in a single trace spanning 65 minutes, and kept emitting
    /// after the client that fired it had closed.
    /// </summary>
    [TestFixture]
    public class CyclicTopologyTests
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
        /// The original, unedited. accounting, receipt and loyalty all publish to
        /// the topic and all consume from it.
        /// </summary>
        private const string PubSubCycle = @"
flowchart TD
    user[User] --> traefik[Traefik]
    traefik --> ui[UI]
    ui --> accounting[Accounting Service]
    ui --> order[Order Service]
    order --> virtualCustomer[Virtual Customer]
    accounting --> receipt[Receipt Service]
    accounting --> loyalty[Loyalty Service]
    accounting --> makeline[Makeline Service]
    makeline --> worker[Virtual Worker]
    accounting --> cosmos[(Azure Cosmos DB)]
    loyalty --> cosmos
    makeline --> redis((Azure Managed Redis))
    worker --> sql[(Azure SQL Database)]
    order --> topic[[Publish Subscribe Topic]]
    accounting --> topic
    receipt --> topic
    loyalty --> topic
    topic --> accounting
    topic --> receipt
    topic --> loyalty
    topic --> worker";

        private const string Acyclic = @"
flowchart TD
    user[User] --> traefik[Traefik]
    traefik --> ui[UI]
    ui --> order[Order Service]
    order --> topic[[Publish Subscribe Topic]]
    topic --> receipt[Receipt Service]
    receipt --> cosmos[(Azure Cosmos DB)]";

        private RunResult Fire(string diagram) =>
            _runner.Run(MermaidParser.Parse(diagram), runIndex: 1, shoeboxId: "test");

        [Test]
        public void A_Cycle_Cannot_Spend_More_Than_The_Budget()
        {
            // The whole point. Unbounded before this: the depth-32 guard bounded
            // path length while the walk multiplied path count, so it never fired.
            Fire(PubSubCycle).SpanCount.Should().BeLessThanOrEqualTo(RunLimits.MaxSpans);
        }

        [Test]
        public void A_Cyclic_Diagram_Completes_Rather_Than_Truncating()
        {
            // The budget is the backstop and should never be what stops a real
            // diagram. Per-path visiting is what makes this one finite, so the run
            // ends because it ran out of paths, not because it ran out of room.
            var result = Fire(PubSubCycle);

            result.SpanCount.Should().BeLessThan(RunLimits.MaxSpans);
            result.Notes.Should().NotContain(n => n.Contains("Walk stopped"));
        }

        [Test]
        public void The_Run_Says_Where_It_Declined_To_Go_Round_Again()
        {
            // A cyclic diagram that reports nothing is how somebody concludes the
            // picture and the trace agree when they do not.
            Fire(PubSubCycle).Notes
                .Should().Contain(n => n.Contains("did not go round again"));
        }

        [Test]
        public void The_Cycle_Warning_Is_One_Sentence_Not_Thousands()
        {
            // state.Note fired on every branch that hit the limit, so a looping
            // diagram produced thousands of identical copies of one note.
            var notes = Fire(PubSubCycle).Notes;
            notes.Should().OnlyHaveUniqueItems();
            notes.Count.Should().BeLessThan(20);
        }

        [Test]
        public void Parse_Names_The_Pods_That_Reach_Themselves()
        {
            // /topology/parse returned "notes":[] on this diagram, which is the
            // endpoint the agent contract sends people to before firing.
            var graph = MermaidParser.Parse(PubSubCycle);

            graph.CyclicPods.Should().BeEquivalentTo(new[] { "accounting", "receipt", "loyalty", "topic" });
            graph.CycleNotes.Should().ContainSingle();
        }

        [Test]
        public void An_Honest_Diagram_Is_Left_Alone()
        {
            // The bound must not change what a normal run produces, and a topic
            // with a one-way consumer is not a cycle.
            var graph = MermaidParser.Parse(Acyclic);
            graph.CyclicPods.Should().BeEmpty();
            graph.CycleNotes.Should().BeEmpty();

            var result = Fire(Acyclic);
            result.SpanCount.Should().BeLessThan(RunLimits.MaxSpans);
            result.Notes.Should().NotContain(n => n.Contains("Walk stopped"));
        }

        [Test]
        public void The_Pub_Sub_Diagram_Leaves_No_Edge_Uncrossed()
        {
            // The reassuring half, and worth pinning because it is the case that
            // started all this. Declining is per path, so an arrow refused on one
            // path is usually crossed on another: ui -> accounting -> topic ->
            // accounting turns back, ui -> order -> topic -> accounting does not.
            // On this diagram every edge is crossed somewhere, so the run is
            // complete in the strongest sense and there is nothing to grey out.
            var result = Fire(PubSubCycle);

            result.NotTaken.Should().BeEmpty();

            var crossed = result.Hops.Select(h => $"{h.From}->{h.To}").ToHashSet();
            var drawn = MermaidParser.Parse(PubSubCycle).Calls
                .Select(c => $"{c.FromId}->{c.ToId}").ToHashSet();
            drawn.Should().OnlyContain(e => crossed.Contains(e), "every arrow the user drew was traversed at least once");
        }

        [Test]
        public void An_Edge_Only_Reachable_By_Repeating_Comes_Back_As_Data()
        {
            // b -> a is reachable only by returning to a, so it is refused every
            // time and never crossed. That is the case a picture must show
            // differently, and a renderer cannot find it by parsing a sentence.
            var result = _runner.Run(MermaidParser.Parse(@"
flowchart TD
    u[User] --> a[Alpha]
    a --> b[Bravo]
    b --> a"), runIndex: 1, shoeboxId: "test");

            result.NotTaken.Should().ContainSingle();
            result.NotTaken[0].From.Should().Be("b", "pod ids, matching Hop, so a renderer keys onto nodes it drew");
            result.NotTaken[0].To.Should().Be("a");
            result.NotTaken[0].Reason.Should().NotBeNullOrWhiteSpace();

            result.Hops.Select(h => $"{h.From}->{h.To}")
                .Should().NotContain("b->a", "an edge reported untaken must not also be reported crossed");
        }

        [Test]
        public void An_Honest_Diagram_Leaves_Nothing_Untaken()
        {
            Fire(Acyclic).NotTaken.Should().BeEmpty();
        }

        [Test]
        public void The_Run_Is_Rooted_At_The_Entry_Pod()
        {
            // The 2026-09-03 trace had no root at all: every one of its 23,428
            // spans carried a parent, and the topmost pointed at a span id that
            // was never exported. The entry activity is held open by the walk, so
            // an unbounded walk never ends it and the root is the one span that
            // cannot survive.
            var result = Fire(PubSubCycle);

            result.TraceId.Should().NotBeNullOrEmpty();
            result.ServedBy.Should().NotBeEmpty();
            result.ServedBy[0].Should().Be("user-1");
        }

        [Test]
        public void A_Self_Call_Counts_As_A_Cycle()
        {
            // The smallest case, and the one a retry loop draws.
            var graph = MermaidParser.Parse(@"
flowchart TD
    a[Alpha] --> b[Bravo]
    b --> b");

            graph.CyclicPods.Should().Contain("b");
        }
    }
}
