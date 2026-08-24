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


        // A phantom is asynchronous or it is nothing. A synchronous callee that is
        // not there refuses the connection, and a refused connection is an error
        // span: evidence, not an absence.
        private const string WithPhantom = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> inv[Inventory API]
  orders --> q[[orders.created]]
  q -->|phantom| pay[Payment Service]";

        [Test]
        public void A_Dead_Consumer_Leaves_No_Trace_Of_Itself()
        {
            var result = Fire(WithPhantom, 1);

            result.ServedBy.Should().NotContain(p => p.StartsWith("payment-service"));
            result.Hops.Should().NotContain(h => h.To == "pay");

            // And nothing failed. That is the whole difficulty: a queue nobody
            // drains looks exactly like a queue somebody drains, from the
            // producer's side.
            result.FailedSpanCount.Should().Be(0);
        }

        [Test]
        public void The_Publish_Still_Happens_Which_Is_What_Makes_It_Inferable()
        {
            // Without the producer's span there is no destination on record and
            // nothing to notice the absence of. The pairing is the signal.
            var result = Fire(WithPhantom, 1);

            result.Hops.Should().Contain(h => h.To == "q");
            result.ServedBy.Should().Contain("orders-api-1");
        }

        [Test]
        public void Nothing_Downstream_Of_A_Dead_Consumer_Happens()
        {
            const string diagram = @"
flowchart LR
  orders[Orders API] --> q[[orders.created]]
  q -->|phantom| pay[Payment Service]
  pay --> ledger[(Ledger DB)]";

            var result = Fire(diagram, 1);

            // A consumer that never ran cannot have called its database.
            result.ServedBy.Should().NotContain(p => p.StartsWith("ledger-db"));
        }

        [Test]
        public void A_Phantom_On_A_Direct_Call_Is_Refused_And_Says_Why()
        {
            const string diagram = @"
flowchart LR
  orders[Orders API] -->|phantom| pay[Payment Service]";

            var result = Fire(diagram, 1);

            // It runs as an ordinary call rather than silently doing nothing, and
            // the note explains that a phantom needs a queue to be a phantom.
            result.ServedBy.Should().Contain("payment-service-1");
            result.Notes.Should().Contain(n => n.Contains("queue"));
        }

    }
}
