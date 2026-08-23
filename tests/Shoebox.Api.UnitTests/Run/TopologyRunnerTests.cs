using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    [TestFixture]
    public class TopologyRunnerTests
    {
        private PodTracerPool _pool = null!;
        private TopologyRunner _runner = null!;

        [SetUp]
        public void SetUp()
        {
            // No export target: these tests are about what the walk produces, not
            // about where it goes.
            _pool = new PodTracerPool(target: null);
            _runner = new TopologyRunner(_pool);
        }

        [TearDown]
        public void TearDown() => _pool.Dispose();

        private RunResult Fire(string diagram, int runIndex) =>
            _runner.Run(MermaidParser.Parse(diagram), runIndex, sandboxId: "test");

        private const string WorkerPermutation = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]
  q --> worker[Worker x5]
  worker --> api[Inventory API]
  worker -->|broken on #3: connection refused| rabbit[[RabbitMQ]]";

        [Test]
        public void Replica_Selection_Walks_Round_Robin_Across_Runs()
        {
            // Deterministic, never random. A link is meant to be a runnable repro,
            // and random selection breaks that promise quietly.
            for (var run = 1; run <= 5; run++)
            {
                Fire(WorkerPermutation, run).ServedBy.Should().Contain($"worker-{run}");
            }
        }

        [Test]
        public void The_Walk_Wraps_Around_Past_The_Replica_Count()
        {
            Fire(WorkerPermutation, 6).ServedBy.Should().Contain("worker-1");
            Fire(WorkerPermutation, 7).ServedBy.Should().Contain("worker-2");
        }

        [Test]
        public void Only_The_Named_Instance_Fails_And_Always_On_The_Same_Run()
        {
            // Four runs look perfect and the fifth press fails, in that order, for
            // anyone who opens the link. That is the lesson the pair exists to teach.
            Fire(WorkerPermutation, 1).FailedSpanCount.Should().Be(0);
            Fire(WorkerPermutation, 2).FailedSpanCount.Should().Be(0);
            Fire(WorkerPermutation, 3).FailedSpanCount.Should().Be(1);
            Fire(WorkerPermutation, 4).FailedSpanCount.Should().Be(0);
            Fire(WorkerPermutation, 5).FailedSpanCount.Should().Be(0);
        }

        [Test]
        public void The_Same_Run_Index_Always_Produces_The_Same_Result()
        {
            var first = Fire(WorkerPermutation, 3);
            var second = Fire(WorkerPermutation, 3);

            second.ServedBy.Should().BeEquivalentTo(first.ServedBy);
            second.SpanCount.Should().Be(first.SpanCount);
            second.FailedSpanCount.Should().Be(first.FailedSpanCount);
        }

        [Test]
        public void A_Bare_Broken_Edge_Fails_On_Every_Run()
        {
            const string diagram = @"
flowchart LR
  api[API] -->|broken: wrong table| db[(SQL Server)]";

            for (var run = 1; run <= 3; run++)
            {
                Fire(diagram, run).FailedSpanCount.Should().Be(1);
            }
        }

        [Test]
        public void A_Healthy_Diagram_Produces_No_Failures()
        {
            const string diagram = @"
flowchart LR
  gw[Gateway] --> orders[Orders]
  orders --> db[(Postgres)]";

            var result = Fire(diagram, 1);

            result.FailedSpanCount.Should().Be(0);
            result.SpanCount.Should().Be(3);
        }

        [Test]
        public void A_Broken_Call_Does_Not_Take_Down_Its_Siblings()
        {
            // One failed child under a parent that still has successful children.
            // That is what a bad connection string looks like, and it reads nothing
            // like a dead service.
            var result = Fire(WorkerPermutation, 3);

            result.FailedSpanCount.Should().Be(1);
            result.SpanCount.Should().BeGreaterThan(result.FailedSpanCount);
        }

        [Test]
        public void Every_Span_Shares_One_Trace()
        {
            var result = Fire(WorkerPermutation, 1);

            result.TraceId.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void A_Diagram_With_No_Entry_Point_Reports_Rather_Than_Throws()
        {
            // Every pod is called by something, so there is nowhere to start.
            var result = Fire("flowchart LR\n  a[A] --> b[B]\n  b --> a", 1);

            result.SpanCount.Should().Be(0);
            result.Notes.Should().NotBeNull();
        }

        [Test]
        public void A_Cycle_Is_Bounded_Rather_Than_Refused()
        {
            // A cycle in a pasted diagram is somebody's real architecture.
            const string diagram = @"
flowchart LR
  entry[Entry] --> a[A]
  a --> b[B]
  b --> a";

            var act = () => Fire(diagram, 1);

            act.Should().NotThrow();
        }

        [Test]
        public void Pinned_Instances_Ignore_The_Run_Index()
        {
            const string diagram = @"
flowchart LR
  q[[Queue]] --> w1[Worker #1]
  q --> w2[Worker #2]";

            var result = Fire(diagram, 7);

            result.ServedBy.Should().Contain("worker-1");
            result.ServedBy.Should().Contain("worker-2");
        }

        [Test]
        public void A_Run_Is_Its_Own_Trace_Even_Under_An_Unsampled_Ambient_Activity()
        {
            // ASP.NET leaves an Activity on the request. With nothing listening to
            // it, it is not sampled, and a parent-based sampler then drops every
            // simulated span beneath it: the run reports zero spans and the person
            // blames their diagram. It also rooted the trace at a service called
            // shoebox, which is not in anybody's diagram.
            using var ambientSource = new ActivitySource("ambient-unsampled");
            using var ambient = new Activity("incoming request").Start();

            const string diagram = @"
flowchart LR
  api[Orders API] --> db[(Postgres)]";

            var result = Fire(diagram, 1);

            result.SpanCount.Should().Be(2, "the simulation must record regardless of what is on Activity.Current");
            Activity.Current.Should().Be(ambient, "the ambient activity has to be put back");
        }


        private const string WithPhantom = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> inv[Inventory API]
  orders -->|phantom| pay[Payment Service]";

        [Test]
        public void A_Phantom_Never_Speaks_For_Itself()
        {
            var result = Fire(WithPhantom, 1);

            // It served nothing, because it never reported serving anything. The
            // caller's span is what names it, and that span belongs to the caller.
            result.ServedBy.Should().NotContain(p => p.StartsWith("payment-service"));
            result.ServedBy.Should().Contain("orders-api-1");

            // And it is not an error. Every span in this trace is a success, which
            // is what makes a phantom harder to notice than a failure.
            result.FailedSpanCount.Should().Be(0);
        }

        [Test]
        public void The_Call_To_A_Phantom_Is_Still_Recorded_By_Whoever_Made_It()
        {
            // Without the caller's span there is nothing naming the phantom at
            // all, and a backend has nothing to infer from. The asymmetry is the
            // signal: named by others, silent itself.
            const string withoutTheEdge = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> inv[Inventory API]
  pay[Payment Service]";

            var withPhantom = Fire(WithPhantom, 1);
            var without = Fire(withoutTheEdge, 1);

            withPhantom.SpanCount.Should().Be(without.SpanCount + 1);
            withPhantom.Hops.Should().Contain(h => h.To == "pay");
        }

        [Test]
        public void Nothing_Downstream_Of_A_Phantom_Happens()
        {
            const string diagram = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders -->|phantom| pay[Payment Service]
  pay --> ledger[(Ledger DB)]";

            var result = Fire(diagram, 1);

            // A service that never ran cannot have called its database.
            result.ServedBy.Should().NotContain(p => p.StartsWith("ledger-db"));
        }

    }
}
