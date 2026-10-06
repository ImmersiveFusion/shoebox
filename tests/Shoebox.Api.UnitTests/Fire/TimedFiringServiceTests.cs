using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Fire;
using Shoebox.Api.Run;

namespace Shoebox.Api.UnitTests.Fire
{
    // Every test here drives a fake clock. Nothing sleeps: a timer that is meant to run for two
    // hours is checked by advancing two hours, which is the only way a test of "it stops by
    // itself" can be both honest and fast.
    [TestFixture]
    public class TimedFiringServiceTests
    {
        private const string Shoebox = "box-1";
        private const string Source = "source:203.0.113.7";
        private const string DiagramA = "flowchart LR\n  a[Orders API] --> b[(Store)]";
        private const string DiagramB = "flowchart LR\n  a[Orders API] -->|broken| b[(Store)]";

        private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

        private FakeTimeProvider _clock = null!;
        private RecordingFirer _firer = null!;
        private TimedFiringService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            _firer = new RecordingFirer();
            _service = Create(new TimedFiringOptions());
        }

        [TearDown]
        public void TearDown() => _service.Dispose();

        private TimedFiringService Create(TimedFiringOptions options) =>
            new(_clock, _firer, Options.Create(options));

        private FiringOutcome StartDefault(TimeSpan? duration = null, TimeSpan? interval = null,
            string shoebox = Shoebox, string source = Source) =>
            _service.Start(shoebox, source, DiagramA, duration ?? TimeSpan.FromMinutes(1), interval);

        // ── 1. No open-ended mode ────────────────────────────────────────────

        [Test]
        public void Start_WithoutADuration_IsRefused()
        {
            var outcome = _service.Start(Shoebox, Source, DiagramA, duration: null, interval: null);

            outcome.Rejection.Should().Be(FiringRejection.Invalid);
            outcome.Message.Should().Contain("duration is required");
            _service.ActiveCount.Should().Be(0);
            _firer.Calls.Should().BeEmpty("a refused start must not have fired anything");
        }

        [TestCase(0)]
        [TestCase(-30)]
        public void Start_WithANonPositiveDuration_IsRefused(int seconds)
        {
            _service.Start(Shoebox, Source, DiagramA, TimeSpan.FromSeconds(seconds), null)
                .Rejection.Should().Be(FiringRejection.Invalid);
            _service.ActiveCount.Should().Be(0);
        }

        [Test]
        public void Start_OverTwoHours_IsRefusedWithAClearMessage()
        {
            var outcome = _service.Start(Shoebox, Source, DiagramA, TwoHours + TimeSpan.FromSeconds(1), null);

            outcome.Rejection.Should().Be(FiringRejection.Invalid);
            outcome.Message.Should().Contain("2 hour ceiling");
            _service.ActiveCount.Should().Be(0);
        }

        [Test]
        public void Start_AtExactlyTwoHours_IsAccepted()
        {
            var outcome = _service.Start(Shoebox, Source, DiagramA, TwoHours, null);

            outcome.Ok.Should().BeTrue();
            outcome.Status!.StopsAt.Should().Be(_clock.GetUtcNow() + TwoHours);
        }

        [Test]
        public void Start_FiresImmediately_ThenOnEveryInterval()
        {
            StartDefault();
            _firer.Calls.Should().HaveCount(1, "somebody who pressed start should see it working at once");

            _clock.Advance(FiveSeconds);
            _clock.Advance(FiveSeconds);

            _firer.Calls.Should().HaveCount(3);
            _firer.Calls.Select(c => c.RunIndex).Should().Equal(1, 2, 3);
            _firer.Calls.Should().OnlyContain(c => c.ShoeboxId == Shoebox);
            _service.Status(Shoebox)!.Runs.Should().Be(3);
        }

        [Test]
        public void StopsByItself_WhenTheTimeIsUp_WithNoAction()
        {
            StartDefault(duration: TimeSpan.FromSeconds(30));

            _clock.Advance(TimeSpan.FromSeconds(29));
            _service.Status(Shoebox)!.Firing.Should().BeTrue();

            _clock.Advance(TimeSpan.FromSeconds(1));
            _service.Status(Shoebox).Should().BeNull("the timer ended itself at the stated time");
            _service.ActiveCount.Should().Be(0);

            // t = 0, 5, 10, 15, 20, 25. The tick due at 30 is the end, not a run.
            _firer.Calls.Should().HaveCount(6);

            _clock.Advance(TimeSpan.FromHours(3));
            _firer.Calls.Should().HaveCount(6, "nothing fires after the end, however long the process lives");
        }

        [Test]
        public void StopsOnTime_EvenWhenTheIntervalWouldOvershootTheEnd()
        {
            // A 60 second interval and a 90 second duration: the end lands between ticks, and must
            // land on time rather than on the tick after it.
            StartDefault(duration: TimeSpan.FromSeconds(90), interval: TimeSpan.FromSeconds(60));

            // Stepped, because a fake clock that jumps straight to 90 delivers the tick due at 60
            // with the time already reading 90, which is not something a real clock does.
            _clock.Advance(TimeSpan.FromSeconds(60));
            _clock.Advance(TimeSpan.FromSeconds(29));
            _service.Status(Shoebox).Should().NotBeNull();

            _clock.Advance(TimeSpan.FromSeconds(1));

            _service.Status(Shoebox).Should().BeNull("the end has its own timer and does not wait for the tick at 120");
            _firer.Calls.Should().HaveCount(2, "t = 0 and t = 60");
        }

        [Test]
        public void Extend_AddsTime()
        {
            StartDefault(duration: TimeSpan.FromMinutes(10));
            var before = _service.Status(Shoebox)!.StopsAt;

            var outcome = _service.Extend(Shoebox, TimeSpan.FromMinutes(15));

            outcome.Ok.Should().BeTrue();
            outcome.Clamped.Should().BeFalse();
            outcome.Status!.StopsAt.Should().Be(before + TimeSpan.FromMinutes(15));

            // And the new end is the one that holds.
            _clock.Advance(TimeSpan.FromMinutes(24));
            _service.Status(Shoebox).Should().NotBeNull();
            _clock.Advance(TimeSpan.FromMinutes(1));
            _service.Status(Shoebox).Should().BeNull();
        }

        [Test]
        public void Extend_IsCapped_SoTimeLeftNeverExceedsTwoHours()
        {
            StartDefault(duration: TimeSpan.FromHours(1));
            _clock.Advance(TimeSpan.FromMinutes(10));

            var outcome = _service.Extend(Shoebox, TwoHours);

            outcome.Ok.Should().BeTrue();
            outcome.Clamped.Should().BeTrue();
            outcome.Message.Should().Contain("2 hour ceiling");
            outcome.Status!.StopsAt.Should().Be(_clock.GetUtcNow() + TwoHours);
            outcome.Status.RemainingSeconds.Should().Be(TwoHours.TotalSeconds);

            // Asking again changes nothing: the ceiling is on time left, not per request.
            _service.Extend(Shoebox, TimeSpan.FromMinutes(5)).Status!.RemainingSeconds
                .Should().Be(TwoHours.TotalSeconds);

            _clock.Advance(TwoHours);
            _service.Status(Shoebox).Should().BeNull();
        }

        [Test]
        public void Extend_ByAnAbsurdAmount_ClampsRatherThanOverflowing()
        {
            StartDefault();

            var outcome = _service.Extend(Shoebox, TimeSpan.MaxValue);

            outcome.Clamped.Should().BeTrue();
            outcome.Status!.RemainingSeconds.Should().Be(TwoHours.TotalSeconds);
        }

        [Test]
        public void Extend_NeedsAPositiveAmount_AndSomethingFiring()
        {
            _service.Extend(Shoebox, TimeSpan.FromMinutes(5)).Rejection.Should().Be(FiringRejection.NotFiring);

            StartDefault();
            _service.Extend(Shoebox, null).Rejection.Should().Be(FiringRejection.Invalid);
            _service.Extend(Shoebox, TimeSpan.Zero).Rejection.Should().Be(FiringRejection.Invalid);
        }

        // ── 2. Visible while on, with a stop ────────────────────────────────

        [Test]
        public void Status_ShowsRunsAndWhenItStops()
        {
            StartDefault(duration: TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromSeconds(10));

            var status = _service.Status(Shoebox)!;

            status.Firing.Should().BeTrue();
            status.Runs.Should().Be(3);
            status.StopsAt.Should().Be(status.StartedAt + TimeSpan.FromMinutes(1));
            status.RemainingSeconds.Should().Be(50);
            status.IntervalSeconds.Should().Be(5);
            status.LastTraceId.Should().Be("trace-3");
        }

        [Test]
        public void Stop_EndsItAtOnce()
        {
            StartDefault();

            _service.Stop(Shoebox).Should().BeTrue();
            _clock.Advance(TimeSpan.FromMinutes(5));

            _firer.Calls.Should().HaveCount(1);
            _service.Status(Shoebox).Should().BeNull();
            _service.Stop(Shoebox).Should().BeFalse("there is nothing left to stop");
        }

        [Test]
        public void Start_WhileAlreadyFiring_IsAConflict_AndAfterStopIsFine()
        {
            StartDefault();

            StartDefault().Rejection.Should().Be(FiringRejection.AlreadyFiring);

            _service.Stop(Shoebox);
            StartDefault().Ok.Should().BeTrue();
        }

        // ── 3. Off is the default and survives a restart ─────────────────────

        [Test]
        public void ANewInstance_HasNothingFiring()
        {
            StartDefault(duration: TwoHours);
            _service.ActiveCount.Should().Be(1);

            // A restart: the old process goes away and a new one comes up with the same
            // configuration. Nothing carries over, because nothing was ever written down.
            _service.Dispose();
            var callsBeforeRestart = _firer.Calls.Count;
            using var restarted = Create(new TimedFiringOptions());

            restarted.ActiveCount.Should().Be(0);
            restarted.Status(Shoebox).Should().BeNull();

            _clock.Advance(TimeSpan.FromMinutes(30));
            _firer.Calls.Should().HaveCount(callsBeforeRestart, "neither the old timer nor the new service fires");
        }

        [Test]
        public void Shutdown_CancelsEveryTimer_AndRefusesNewOnes()
        {
            StartDefault(shoebox: "box-a", source: "source:a");
            StartDefault(shoebox: "box-b", source: "source:b");

            _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            _service.ActiveCount.Should().Be(0);
            var calls = _firer.Calls.Count;
            _clock.Advance(TimeSpan.FromMinutes(1));
            _firer.Calls.Should().HaveCount(calls);
            _firer.Tokens.Should().OnlyContain(t => t.IsCancellationRequested);

            StartDefault(shoebox: "box-c", source: "source:c").Rejection.Should().Be(FiringRejection.Unavailable);
        }

        // ── 4. Edits take effect on the next run ─────────────────────────────

        [Test]
        public void ADiagramEdit_IsWhatTheNextRunWalks()
        {
            StartDefault();
            _firer.Calls.Last().Diagram.Should().Be(DiagramA);

            _service.UpdateDiagram(Shoebox, DiagramB).Ok.Should().BeTrue();
            _clock.Advance(FiveSeconds);

            _firer.Calls.Last().Diagram.Should().Be(DiagramB);
        }

        [Test]
        public void UpdateDiagram_NeedsADiagram_AndSomethingFiring()
        {
            _service.UpdateDiagram(Shoebox, DiagramB).Rejection.Should().Be(FiringRejection.NotFiring);

            StartDefault();
            _service.UpdateDiagram(Shoebox, "  ").Rejection.Should().Be(FiringRejection.Invalid);
        }

        // ── 5. Rate bounds ───────────────────────────────────────────────────

        [Test]
        public void Rate_DefaultsToOneRunEveryFiveSeconds()
        {
            StartDefault(interval: null).Status!.IntervalSeconds.Should().Be(5);
        }

        [TestCase(0.5)]
        [TestCase(0)]
        [TestCase(60.5)]
        [TestCase(3600)]
        public void Rate_OutsideOneToSixtySeconds_IsRefused(double seconds)
        {
            var outcome = StartDefault(interval: TimeSpan.FromSeconds(seconds));

            outcome.Rejection.Should().Be(FiringRejection.Invalid);
            outcome.Message.Should().Contain("intervalSeconds");
            _service.ActiveCount.Should().Be(0);
        }

        [TestCase(1)]
        [TestCase(60)]
        public void Rate_AtTheBounds_IsAccepted(double seconds)
        {
            StartDefault(interval: TimeSpan.FromSeconds(seconds)).Ok.Should().BeTrue();
        }

        // ── 6. Safety ────────────────────────────────────────────────────────

        [Test]
        public void ATick_WhileTheLastRunIsStillGoing_IsSkipped_NotQueued()
        {
            var slow = new TaskCompletionSource<RunResult>();
            _firer.Next = () => slow.Task;

            StartDefault();
            _clock.Advance(FiveSeconds);
            _clock.Advance(FiveSeconds);
            _clock.Advance(FiveSeconds);

            _firer.Calls.Should().HaveCount(1, "the first run never finished");
            _service.Status(Shoebox)!.Skipped.Should().Be(3);

            _firer.Next = null;
            slow.SetResult(RecordingFirer.Result(1));

            // Finishing does not release a backlog: the skipped ticks are gone, and the next run
            // comes on the next tick, alone.
            _firer.Calls.Should().HaveCount(1);
            _clock.Advance(FiveSeconds);
            _firer.Calls.Should().HaveCount(2);
        }

        [Test]
        public void AFailingRun_DoesNotEndTheTimer()
        {
            _firer.Next = () => Task.FromException<RunResult>(new InvalidOperationException("boom"));
            StartDefault();

            _service.Status(Shoebox)!.LastError.Should().Be("boom");

            _firer.Next = null;
            _clock.Advance(FiveSeconds);

            var status = _service.Status(Shoebox)!;
            status.Runs.Should().Be(2);
            status.LastError.Should().BeNull();
        }

        [Test]
        public void ThereIsAGlobalCap_OnTimersPerProcess()
        {
            _service.Dispose();
            _service = Create(new TimedFiringOptions { MaxActive = 2 });

            StartDefault(shoebox: "box-a", source: "source:a").Ok.Should().BeTrue();
            StartDefault(shoebox: "box-b", source: "source:b").Ok.Should().BeTrue();

            var third = StartDefault(shoebox: "box-c", source: "source:c");
            third.Rejection.Should().Be(FiringRejection.InstanceFull);
            third.Message.Should().Contain("slot");

            // A slot comes back when a timer ends.
            _service.Stop("box-a");
            StartDefault(shoebox: "box-c", source: "source:c").Ok.Should().BeTrue();
        }

        [Test]
        public void OneSource_CannotTakeEverySlot()
        {
            StartDefault(shoebox: "box-a").Ok.Should().BeTrue();

            StartDefault(shoebox: "box-b").Rejection.Should().Be(FiringRejection.SourceFull);
        }

        [Test]
        public void ZeroSlots_SwitchesTimedFiringOff()
        {
            _service.Dispose();
            _service = Create(new TimedFiringOptions { MaxActive = 0 });

            StartDefault().Rejection.Should().Be(FiringRejection.Unavailable);
        }

        [Test]
        public void NonsenseConfiguration_IsRefusedAtStartup()
        {
            var act = () => Create(new TimedFiringOptions
            {
                MinInterval = TimeSpan.FromSeconds(10),
                MaxInterval = TimeSpan.FromSeconds(5),
            });

            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Start_NeedsAShoeboxAndADiagram()
        {
            _service.Start(null, Source, DiagramA, TimeSpan.FromMinutes(1), null)
                .Rejection.Should().Be(FiringRejection.Invalid);
            _service.Start(Shoebox, Source, "", TimeSpan.FromMinutes(1), null)
                .Rejection.Should().Be(FiringRejection.Invalid);
        }

        // ── What a timer will hold ───────────────────────────────────────────

        [Test]
        public void ADiagramOverTheSizeCap_IsRefused_AtStartAndOnEdit()
        {
            var huge = DiagramA + "\n%% " + new string('x', TimedFiringService.MaxDiagramBytes);

            var start = _service.Start(Shoebox, Source, huge, TimeSpan.FromMinutes(1), null);
            start.Rejection.Should().Be(FiringRejection.TooLarge);
            start.Message.Should().Contain("256 KB");
            _service.ActiveCount.Should().Be(0);

            StartDefault();
            _service.UpdateDiagram(Shoebox, huge).Rejection.Should().Be(FiringRejection.TooLarge);
            _clock.Advance(FiveSeconds);
            _firer.Calls.Last().Diagram.Should().Be(DiagramA, "a refused edit leaves the last good diagram in place");
        }

        [TestCase("flowchart LR\n  %% nothing here")]
        [TestCase("flowchart LR\n  a[A] --> b[B]\n  b --> a")]
        [TestCase("this is not mermaid at all")]
        public void ADiagramThatWouldNotRun_IsRefusedWhenSent_NotDiscoveredLater(string diagram)
        {
            var start = _service.Start(Shoebox, Source, diagram, TimeSpan.FromMinutes(1), null);

            start.Rejection.Should().Be(FiringRejection.Invalid);
            start.Message.Should().Contain("would not run");
            _firer.Calls.Should().BeEmpty();

            StartDefault();
            _service.UpdateDiagram(Shoebox, diagram).Rejection.Should().Be(FiringRejection.Invalid);
        }

        // ── Extending at the very end ────────────────────────────────────────

        [Test]
        public void Extend_AfterTheEndButBeforeTheExpiryRuns_IsNotFiring_NotAnError()
        {
            // The race: the clock has passed the end, and the expiry callback has not run yet.
            // A clock that reads ahead of the timers it hands out reproduces it exactly.
            var skewed = new SkewedClock(_clock);
            using var service = new TimedFiringService(skewed, _firer, Options.Create(new TimedFiringOptions()));
            service.Start(Shoebox, Source, DiagramA, TimeSpan.FromMinutes(1), null).Ok.Should().BeTrue();

            skewed.Skew = TimeSpan.FromMinutes(1);

            var act = () => service.Extend(Shoebox, TimeSpan.FromMinutes(5));
            act.Should().NotThrow();
            act().Rejection.Should().Be(FiringRejection.NotFiring);
        }

        /// <summary>Hands out the fake clock's timers, but reads the time <see cref="Skew"/> ahead of it.</summary>
        private sealed class SkewedClock(FakeTimeProvider inner) : TimeProvider
        {
            public TimeSpan Skew { get; set; }

            public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Skew;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
                inner.CreateTimer(callback, state, dueTime, period);
        }

        // ── The real firer ───────────────────────────────────────────────────

        [Test]
        public async Task TheRealFirer_ParsesAndWalksTheDiagramItIsGiven()
        {
            using var pool = new PodTracerPool(target: null);
            var firer = new TopologyRunFirer(new TopologyRunner(pool));

            var result = await firer.FireAsync(DiagramB, 1, Shoebox, CancellationToken.None);

            result.RunIndex.Should().Be(1);
            result.SpanCount.Should().BeGreaterThan(0);
            result.FailedSpanCount.Should().BeGreaterThan(0, "the diagram it was handed has a broken edge");
        }

        /// <summary>Records every run it is asked for, and completes it at once unless told otherwise.</summary>
        private sealed class RecordingFirer : IRunFirer
        {
            public List<(string Diagram, int RunIndex, string ShoeboxId)> Calls { get; } = new();
            public List<CancellationToken> Tokens { get; } = new();
            public Func<Task<RunResult>>? Next { get; set; }

            public Task<RunResult> FireAsync(string diagram, int runIndex, string shoeboxId, CancellationToken cancellationToken)
            {
                Calls.Add((diagram, runIndex, shoeboxId));
                Tokens.Add(cancellationToken);
                return Next?.Invoke() ?? Task.FromResult(Result(runIndex));
            }

            public static RunResult Result(int runIndex) => new(
                runIndex, $"trace-{runIndex}", Array.Empty<string>(), 1, 0,
                Array.Empty<string>(), Array.Empty<Hop>(), Array.Empty<NotTaken>());
        }
    }
}
