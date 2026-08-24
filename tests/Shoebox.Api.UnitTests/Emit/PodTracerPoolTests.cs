using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;

namespace Shoebox.Api.UnitTests.Emit
{
    /// <summary>
    /// The runner tests all build a pool with no export target, so they prove the
    /// walk and prove nothing about the thing that actually ships. A deployed
    /// instance always has a target.
    /// </summary>
    [TestFixture]
    public class PodTracerPoolTests
    {
        [Test]
        public void A_Pod_Records_Spans_With_No_Target()
        {
            using var pool = new PodTracerPool(target: null);

            using var activity = pool.For("orders-api", 1).StartActivity("orders-api handle");

            activity.Should().NotBeNull("a pod with nowhere to export still has to record");
        }

        [Test]
        public void A_Pod_Records_Spans_With_A_Target_It_Cannot_Reach()
        {
            // Unreachable on purpose. Export is best effort and a dead backend must
            // never stop a run from being recorded, or the UI reports zero spans and
            // the person blames their diagram.
            OtlpTarget.TryParseEndpoint("https://unreachable.invalid:4317", out var uri, out _).Should().BeTrue();
            using var pool = new PodTracerPool(new OtlpTarget(uri!, string.Empty));

            using var activity = pool.For("orders-api", 1).StartActivity("orders-api handle");

            activity.Should().NotBeNull("an exporter that cannot connect must not silence the tracer");
        }
    }
}
