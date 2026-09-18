using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Emit
{
    /// <summary>
    /// Every span carries <c>ot=th:0</c>, so a backend can tell this stream is
    /// complete rather than sampled, and every span is still recorded and sampled.
    /// </summary>
    [TestFixture]
    public class DeclaredFullSamplerTests
    {
        [Test]
        public void A_Root_Span_Declares_Full_Sampling()
        {
            using var pool = new PodTracerPool(target: null);

            using var root = pool.For("orders-api", 1).StartActivity("orders-api handle");

            root.Should().NotBeNull();
            root!.TraceStateString.Should().Be("ot=th:0");
        }

        [Test]
        public void A_Child_Span_Declares_Full_Sampling()
        {
            using var pool = new PodTracerPool(target: null);
            var source = pool.For("orders-api", 1);

            using var root = source.StartActivity("orders-api handle");
            using var child = source.StartActivity("orders-api query", ActivityKind.Client, root!.Context);

            child.Should().NotBeNull();
            child!.TraceStateString.Should().Be("ot=th:0");
        }

        [Test]
        public void A_Child_On_Another_Pod_Declares_Full_Sampling()
        {
            // Shoebox hands a parent's context to a different pod's provider, which
            // is how one process presents as many services in one trace.
            using var pool = new PodTracerPool(target: null);

            using var root = pool.For("gateway", 1).StartActivity("gateway handle", ActivityKind.Server);
            using var child = pool.For("orders-api", 2).StartActivity("orders-api handle", ActivityKind.Server, root!.Context);

            child.Should().NotBeNull();
            child!.TraceId.Should().Be(root.TraceId);
            child.TraceStateString.Should().Be("ot=th:0");
        }

        [Test]
        public void Other_Vendors_In_The_Parent_Tracestate_Are_Kept_Behind_Ot()
        {
            using var pool = new PodTracerPool(target: null);
            var parent = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                traceState: "foo=bar",
                isRemote: true);

            using var child = pool.For("orders-api", 1).StartActivity("orders-api handle", ActivityKind.Server, parent);

            child.Should().NotBeNull();
            child!.TraceStateString.Should().Be("ot=th:0,foo=bar");
        }

        [Test]
        public void Every_Span_Is_Still_Recorded_And_Sampled()
        {
            const string diagram = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]
  q --> worker[Worker x2]
  worker --> db[(Orders DB)]";

            var seen = new List<Activity>();
            // Sample returns None, so this listener only observes: the pool's own
            // sampler still decides, because the highest decision wins.
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
                ActivityStopped = a => { lock (seen) seen.Add(a); },
            };
            ActivitySource.AddActivityListener(listener);

            using var pool = new PodTracerPool(target: null);
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), 1, shoeboxId: "test");

            List<Activity> run;
            lock (seen) run = seen.Where(a => a.TraceId.ToString() == result.TraceId).ToList();

            result.SpanCount.Should().BeGreaterThan(0);
            run.Should().HaveCount(result.SpanCount, "every span the run counted must have been emitted");
            run.Should().OnlyContain(a => a.Recorded && a.IsAllDataRequested);
            run.Should().OnlyContain(a => a.TraceStateString == "ot=th:0");
        }
    }

    [TestFixture]
    public class TraceStateOtTests
    {
        [TestCase(null, "ot=th:0")]
        [TestCase("", "ot=th:0")]
        [TestCase("foo=bar", "ot=th:0,foo=bar")]
        [TestCase("ot=rv:abc123", "ot=th:0;rv:abc123")]
        [TestCase("ot=th:8", "ot=th:0")]
        [TestCase("ot=th:8;rv:abc123", "ot=th:0;rv:abc123")]
        [TestCase("foo=bar,ot=th:c;rv:01,baz=qux", "ot=th:0;rv:01,foo=bar,baz=qux")]
        [TestCase("foo=bar , baz=qux", "ot=th:0,foo=bar,baz=qux")]
        public void Sets_Th_To_Zero_And_Moves_Ot_To_The_Front(string? input, string expected)
        {
            TraceStateOt.WithFullThreshold(input).Should().Be(expected);
        }

        [Test]
        public void Never_Exceeds_Thirty_Two_List_Members()
        {
            var full = string.Join(',', Enumerable.Range(1, 32).Select(i => $"v{i}=x"));

            var members = TraceStateOt.WithFullThreshold(full).Split(',');

            members.Should().HaveCount(32);
            members[0].Should().Be("ot=th:0");
            members[1].Should().Be("v1=x");
            members[31].Should().Be("v31=x", "the right-most member is the one dropped");
        }
    }
}
