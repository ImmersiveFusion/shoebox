using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// A queue drawn as the last node in a diagram.
    ///
    /// It emits exactly what a declared phantom emits, which is why a backend
    /// marks it unconsumed without anyone having written "phantom". These tests
    /// pin that equivalence rather than paper over it, and pin the sentence that
    /// now says so, because the telemetry is honest and the surprise was silence.
    /// </summary>
    [TestFixture]
    public class TerminalQueueTests
    {
        private const string Terminal = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]";

        private const string Declared = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[orders.created]]
  q -->|phantom| pay[Payment Service]";

        private const string Drained = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]
  q --> worker[Worker]";

        private const string Broken = @"
flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]
  q -->|broken: handler threw| worker[Worker]";

        private static (RunResult Result, List<Activity> Spans) Fire(string diagram)
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
            var result = new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), 1, "test");
            return (result, captured);
        }

        [Test]
        public void A_Terminal_Queue_Emits_What_A_Declared_Phantom_Emits()
        {
            var terminal = Fire(Terminal).Spans;
            var declared = Fire(Declared).Spans;

            // One publish, no receive, in both. This is the finding: a backend has
            // nothing to tell them apart with, so it does not try.
            terminal.Should().ContainSingle(s => s.Kind == ActivityKind.Producer);
            declared.Should().ContainSingle(s => s.Kind == ActivityKind.Producer);

            terminal.Should().NotContain(s => s.Kind == ActivityKind.Consumer);
            declared.Should().NotContain(s => s.Kind == ActivityKind.Consumer);
        }

        [Test]
        public void A_Terminal_Queue_Says_So_Rather_Than_Going_Quiet()
        {
            var notes = Fire(Terminal).Result.Notes;

            notes.Should().ContainSingle(n => n.Contains("Nothing is drawn consuming"),
                "the surprise was silence, not the telemetry");
            notes.Single(n => n.Contains("Nothing is drawn consuming"))
                .Should().Contain("Job Queue").And.Contain("phantom");
        }

        [Test]
        public void A_Declared_Phantom_Is_Not_Told_Twice()
        {
            var notes = Fire(Declared).Result.Notes;

            notes.Should().NotContain(n => n.Contains("Nothing is drawn consuming"));
            notes.Should().Contain(n => n.Contains("Nothing consumed what was published"),
                "the declared phantom keeps its own note");
        }

        [Test]
        public void A_Drained_Queue_Is_Not_Warned_About()
        {
            var (result, spans) = Fire(Drained);

            spans.Should().Contain(s => s.Kind == ActivityKind.Consumer);
            result.Notes.Should().NotContain(n => n.Contains("Nothing is drawn consuming"));
        }

        [Test]
        public void A_Queue_Whose_Only_Consumer_Fails_Also_Has_No_Receive()
        {
            var (result, spans) = Fire(Broken);

            spans.Should().NotContain(s => s.Kind == ActivityKind.Consumer);
            result.Notes.Should().Contain(n => n.Contains("Every consumer of Job Queue failed"),
                "a consumer is drawn, so the note is about the failure and not the drawing");
        }

        [TestCase("Kafka", "kafka")]
        [TestCase("orders.created", "rabbitmq")]
        [TestCase("Job Queue", "rabbitmq")]
        [TestCase("Orders SQS", "aws_sqs")]
        [TestCase("Azure Service Bus", "servicebus")]
        [TestCase("Pulsar", "pulsar")]
        public void The_Broker_Is_Read_Off_The_Label_And_Not_Asserted(string label, string expected)
        {
            // It is not only a wrong attribute. A reader keys a messaging node on
            // (system, destination) and labels it with the system, so hardcoding
            // rabbitmq made every queue in every diagram render under one name.
            var spans = Fire($@"
flowchart LR
  orders[Orders API] --> q[[{label}]]").Spans;

            var publish = spans.Single(s => s.Kind == ActivityKind.Producer);
            publish.GetTagItem("messaging.system").Should().Be(expected);
        }
    }
}
