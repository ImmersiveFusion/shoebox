using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// Everything else asserts on the RunResult, which is what the UI reads. This
    /// listens to the spans themselves, which is what a backend reads, and they are
    /// not the same thing. The phantom lives or dies on what is in the telemetry.
    /// </summary>
    [TestFixture]
    public class PhantomEmissionTests
    {
        private const string DeadConsumer = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> inv[Inventory API]
  orders --> q[[orders.created]]
  q -->|phantom| pay[Payment Service]";

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
            new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), 1, "test");

            return captured;
        }

        [Test]
        public void No_Span_Anywhere_Names_The_Phantom()
        {
            var spans = Capture(DeadConsumer);

            spans.Should().NotBeEmpty();

            foreach (var span in spans)
            {
                span.Source.Name.Should().NotContain("payment-service", "the phantom must not emit");
                span.DisplayName.Should().NotContain("payment", "no span name may mention it");

                foreach (var tag in span.TagObjects)
                {
                    (tag.Value?.ToString() ?? string.Empty)
                        .Should().NotContain("payment", $"{tag.Key} must not name the phantom");
                }
            }
        }

        [Test]
        public void The_Publish_Is_A_Producer_Span_Carrying_The_Destination_Both_Ways()
        {
            var spans = Capture(DeadConsumer);

            var publish = spans.SingleOrDefault(s => s.Kind == ActivityKind.Producer);
            publish.Should().NotBeNull("a queue without a publish cannot be noticed as unconsumed");

            publish!.GetTagItem("messaging.system").Should().Be("rabbitmq");

            // Current spelling, and the deprecated one readers still key on.
            publish.GetTagItem("messaging.destination.name").Should().Be("orders-created");
            publish.GetTagItem("messaging.destination").Should().Be("orders-created");

            // send, not "publish": the enum is create/send/receive/process/settle.
            publish.GetTagItem("messaging.operation.type").Should().Be("send");
        }

        [Test]
        public void Nothing_Consumes_It()
        {
            var spans = Capture(DeadConsumer);

            spans.Should().NotContain(s => s.Kind == ActivityKind.Consumer,
                "a consumer span is exactly what a dead consumer does not produce");
        }
    }
}
