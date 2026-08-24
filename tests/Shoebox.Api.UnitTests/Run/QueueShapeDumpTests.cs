using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// The shape of a healthy queue, which is what a phantom is measured against.
    /// </summary>
    [TestFixture]
    public class QueueShapeTests
    {
        private const string Drained = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[orders.created]]
  q --> pay[Payment Service]";

        private static List<Activity> Capture(string diagram)
        {
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            using var pool = new PodTracerPool(target: null);
            new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), 1, "t");
            return captured;
        }

        [Test]
        public void The_Publish_And_The_Receive_Are_One_Trace_And_Correctly_Parented()
        {
            var spans = Capture(Drained);

            spans.Select(s => s.TraceId).Distinct().Should().HaveCount(1, "a run is one trace");

            var publish = spans.Single(s => s.Kind == ActivityKind.Producer);
            var consume = spans.Single(s => s.Kind == ActivityKind.Consumer);

            consume.ParentSpanId.Should().Be(publish.SpanId,
                "the receive hangs off the publish, which is how the two halves correlate");
        }

        [Test]
        public void A_Consumer_Carries_Messaging_Semantics_And_Not_Http_Ones()
        {
            var consume = Capture(Drained).Single(s => s.Kind == ActivityKind.Consumer);

            consume.GetTagItem("messaging.destination.name").Should().Be("orders-created");

            // It was reached off a queue, not over HTTP. Saying both leaves anything
            // working out how this service is called with two contradictory answers.
            consume.GetTagItem("http.request.method").Should().BeNull();
            consume.GetTagItem("http.route").Should().BeNull();
        }

        [Test]
        public void A_Drained_Queue_Has_A_Consumer_And_Is_Therefore_Not_A_Phantom()
        {
            Capture(Drained).Should().Contain(s => s.Kind == ActivityKind.Consumer);
        }
    }
}
