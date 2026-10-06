using System.Text;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.Fire;

/// <summary>Why a timed-firing request was refused, which the endpoint turns into a status code.</summary>
public enum FiringRejection
{
    None,
    Invalid,
    TooLarge,
    NotFiring,
    AlreadyFiring,
    InstanceFull,
    SourceFull,
    Unavailable,
}

/// <summary>What a timer looks like from outside, for the status endpoint and the UI.</summary>
public sealed record FiringStatus(
    bool Firing,
    string ShoeboxId,
    DateTimeOffset StartedAt,
    DateTimeOffset StopsAt,
    double RemainingSeconds,
    double IntervalSeconds,
    int Runs,
    int Skipped,
    string? LastTraceId,
    string? LastError);

public sealed record FiringOutcome(FiringRejection Rejection, string? Message, FiringStatus? Status, bool Clamped = false)
{
    public bool Ok => Rejection == FiringRejection.None;

    internal static FiringOutcome Refuse(FiringRejection why, string message) => new(why, message, null);
}

/// <summary>
/// Fires the current diagram of a shoebox on a schedule, for a bounded time, and never longer.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is in memory and nothing is ever written anywhere. A restart or a deploy
/// starts this with no timers at all, so off is the state the process comes back in, always.
/// That is the same rule as every other piece of per-shoebox state: held in memory, deliberately
/// not persisted.
/// </para>
/// <para>
/// "The current diagram" is whatever the shoebox's owner last sent. The server holds no diagram
/// otherwise; the diagram lives in the browser and travels in the body of each run. A timer needs
/// one between runs, so it keeps the latest it was given, for as long as it is firing and no
/// longer, and every tick parses that text afresh. An edit sent while firing is therefore the
/// diagram the very next run walks.
/// </para>
/// <para>
/// Safety, in the order it matters: a duration is required and capped at
/// <see cref="TimedFiringLimits.MaxDuration"/>, at start and after every extension; each timer
/// stops itself when its time is up; a tick that finds the previous run still going is skipped,
/// not queued; the number of timers per process and per source address is capped; and shutdown
/// cancels every timer.
/// </para>
/// <para>
/// Time comes from an injected <see cref="TimeProvider"/>, so the tests advance a fake clock
/// rather than sleeping.
/// </para>
/// </remarks>
public sealed class TimedFiringService : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Firing> _active = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly IRunFirer _firer;
    private readonly TimedFiringOptions _options;
    private readonly ILogger<TimedFiringService>? _logger;
    private bool _shutDown;

    /// <summary>
    /// The largest diagram a timer will hold, in UTF-8 bytes. A timer keeps its diagram in memory for
    /// up to two hours, so this is what bounds that memory; it is far past any diagram a person draws.
    /// </summary>
    public const int MaxDiagramBytes = 256 * 1024;

    public TimedFiringService(
        TimeProvider time,
        IRunFirer firer,
        IOptions<TimedFiringOptions> options,
        ILogger<TimedFiringService>? logger = null)
    {
        _time = time;
        _firer = firer;
        _options = options.Value;
        _options.Validate();
        _logger = logger;
    }

    /// <summary>How many timers are running right now, across every shoebox.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    public TimeSpan DefaultInterval => _options.DefaultInterval;

    /// <summary>
    /// Starts firing <paramref name="diagram"/> for <paramref name="shoeboxId"/>. The first run goes
    /// immediately, then one every <paramref name="interval"/> until <paramref name="duration"/> is up.
    /// </summary>
    public FiringOutcome Start(string? shoeboxId, string? source, string? diagram, TimeSpan? duration, TimeSpan? interval)
    {
        if (string.IsNullOrWhiteSpace(shoeboxId))
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid,
                "a shoeboxId is required: it is how the runs are found again, as shoebox.id on every span");
        }

        if (CheckDiagram(diagram) is { } refused) return refused;

        if (duration is null || duration <= TimeSpan.Zero)
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid,
                $"a duration is required: timed firing never runs open-ended. Send durationSeconds, at most {Seconds(TimedFiringLimits.MaxDuration)}");
        }

        if (duration > TimedFiringLimits.MaxDuration)
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid,
                $"durationSeconds is over the 2 hour ceiling ({Seconds(TimedFiringLimits.MaxDuration)}). Ask for less, and extend later if you still need it");
        }

        var every = interval ?? _options.DefaultInterval;
        if (every < _options.MinInterval || every > _options.MaxInterval)
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid,
                $"intervalSeconds must be between {_options.MinInterval.TotalSeconds:0.###} and {_options.MaxInterval.TotalSeconds:0.###} seconds");
        }

        Firing firing;
        lock (_gate)
        {
            if (_shutDown)
            {
                return FiringOutcome.Refuse(FiringRejection.Unavailable, "this instance is shutting down");
            }

            if (_active.ContainsKey(shoeboxId))
            {
                return FiringOutcome.Refuse(FiringRejection.AlreadyFiring,
                    "this shoebox is already firing. Extend it, send it a new diagram, or stop it first");
            }

            if (_options.MaxActive == 0)
            {
                return FiringOutcome.Refuse(FiringRejection.Unavailable, "timed firing is switched off on this instance");
            }

            if (_active.Count >= _options.MaxActive)
            {
                return FiringOutcome.Refuse(FiringRejection.InstanceFull,
                    $"every timed-firing slot on this instance is in use ({_options.MaxActive}). Try again later, or fire by hand");
            }

            var sourceKey = source ?? string.Empty;
            if (_active.Values.Count(f => f.Source == sourceKey) >= _options.MaxPerSource)
            {
                return FiringOutcome.Refuse(FiringRejection.SourceFull,
                    $"your address already has {_options.MaxPerSource} shoebox firing on a timer. Stop it first");
            }

            var now = _time.GetUtcNow();
            firing = new Firing(shoeboxId, sourceKey, diagram, now, now + duration.Value, every);
            _active[shoeboxId] = firing;

            // Two timers: one for the runs, one for the end. The end has its own so that it lands
            // on the stated time rather than on whichever tick happens to come after it, which with
            // a 60 second interval could be most of a minute late.
            firing.Ticker = _time.CreateTimer(_ => Tick(firing), null, every, every);
            firing.Expiry = _time.CreateTimer(_ => Expire(firing), null, duration.Value, Timeout.InfiniteTimeSpan);
        }

        _logger?.LogInformation("Timed firing started for {ShoeboxId}: every {Interval}, until {StopsAt:o}",
            shoeboxId, every, firing.StopsAt);

        // The first run goes now. Somebody who pressed start should see it working, not wait an
        // interval to find out whether it did anything.
        Tick(firing);

        return new FiringOutcome(FiringRejection.None, null, Status(shoeboxId));
    }

    /// <summary>
    /// Adds time. Clamped, not refused, when it would take the time left past the ceiling: the
    /// answer to "fifteen more minutes" near the limit is "as many as are allowed", and the
    /// response says it was clamped.
    /// </summary>
    public FiringOutcome Extend(string? shoeboxId, TimeSpan? by)
    {
        if (by is null || by <= TimeSpan.Zero)
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid, "an extension needs a positive number of seconds");
        }

        lock (_gate)
        {
            if (shoeboxId is null || !_active.TryGetValue(shoeboxId, out var firing))
            {
                return FiringOutcome.Refuse(FiringRejection.NotFiring, "this shoebox is not firing, so there is nothing to extend");
            }

            var now = _time.GetUtcNow();

            // Its end has arrived and the expiry callback has not run yet. Treated as already
            // ended rather than revived: extending is for a timer that is still running.
            if (firing.Ended || firing.StopsAt <= now)
            {
                return FiringOutcome.Refuse(FiringRejection.NotFiring, "this shoebox's timer has just ended, so there is nothing to extend");
            }

            var ceiling = now + TimedFiringLimits.MaxDuration;
            // Compared before adding, so an absurd extension cannot overflow the clock.
            var clamped = by.Value >= TimedFiringLimits.MaxDuration || firing.StopsAt + by.Value > ceiling;

            firing.StopsAt = clamped ? ceiling : firing.StopsAt + by.Value;
            var dueIn = firing.StopsAt - now;
            firing.Expiry?.Change(dueIn > TimeSpan.Zero ? dueIn : TimeSpan.Zero, Timeout.InfiniteTimeSpan);

            return new FiringOutcome(FiringRejection.None, clamped
                    ? "extended only as far as the 2 hour ceiling on time left"
                    : null,
                StatusOf(firing, now), clamped);
        }
    }

    /// <summary>The diagram the next run walks. Takes effect on the next tick, with no restart.</summary>
    public FiringOutcome UpdateDiagram(string? shoeboxId, string? diagram)
    {
        if (CheckDiagram(diagram) is { } refused) return refused;

        lock (_gate)
        {
            if (shoeboxId is null || !_active.TryGetValue(shoeboxId, out var firing))
            {
                return FiringOutcome.Refuse(FiringRejection.NotFiring, "this shoebox is not firing");
            }

            firing.Diagram = diagram!;
            return new FiringOutcome(FiringRejection.None, null, StatusOf(firing, _time.GetUtcNow()));
        }
    }

    /// <summary>Stops firing. True when there was something to stop.</summary>
    public bool Stop(string? shoeboxId)
    {
        Firing? firing;
        lock (_gate)
        {
            if (shoeboxId is null || !_active.TryGetValue(shoeboxId, out firing)) return false;
        }

        End(firing, "stopped");
        return true;
    }

    /// <summary>The timer for this shoebox, or null when it is not firing.</summary>
    public FiringStatus? Status(string? shoeboxId)
    {
        lock (_gate)
        {
            return shoeboxId is not null && _active.TryGetValue(shoeboxId, out var firing)
                ? StatusOf(firing, _time.GetUtcNow())
                : null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Shutdown cancels every timer and refuses new ones.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopAll();
        return Task.CompletedTask;
    }

    public void Dispose() => StopAll();

    private void StopAll()
    {
        List<Firing> all;
        lock (_gate)
        {
            _shutDown = true;
            all = _active.Values.ToList();
        }

        foreach (var firing in all) End(firing, "shutdown");
    }

    private void Tick(Firing firing)
    {
        string diagram;
        int runIndex;
        CancellationToken token = default;
        var expired = false;
        lock (_gate)
        {
            if (firing.Ended) return;

            // Belt and braces with the expiry timer: a tick that lands at or after the end never
            // fires, whichever of the two callbacks the clock happens to deliver first.
            if (_time.GetUtcNow() >= firing.StopsAt)
            {
                expired = true;
                diagram = string.Empty;
                runIndex = 0;
            }
            else if (firing.InFlight)
            {
                // Skipped, not queued. A queue behind a slow run is how a bounded rate quietly
                // becomes a burst the moment the slow run finishes.
                firing.Skipped++;
                return;
            }
            else
            {
                firing.InFlight = true;
                firing.Runs++;
                runIndex = firing.Runs;

                // Read now, not at start: the latest diagram this shoebox sent is the one this
                // run walks.
                diagram = firing.Diagram;

                // Taken under the lock, while the timer is known not to have ended, because End
                // disposes the source once it has.
                token = firing.Cancellation.Token;
            }
        }

        if (expired)
        {
            End(firing, "time up");
            return;
        }

        Task<RunResult> run;
        try
        {
            run = _firer.FireAsync(diagram, runIndex, firing.ShoeboxId, token);
        }
        catch (Exception ex)
        {
            run = Task.FromException<RunResult>(ex);
        }

        run.ContinueWith(t => Completed(firing, t), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Completed(Firing firing, Task<RunResult> run)
    {
        lock (_gate)
        {
            firing.InFlight = false;
            if (run.IsCompletedSuccessfully)
            {
                firing.LastTraceId = run.Result.TraceId;
                firing.LastError = null;
            }
            else if (run.IsFaulted)
            {
                // One bad run does not end the timer: the next tick may well carry a fixed diagram.
                firing.LastError = run.Exception?.GetBaseException().Message;
                _logger?.LogWarning(run.Exception, "Timed run failed for {ShoeboxId}", firing.ShoeboxId);
            }
        }
    }

    private void Expire(Firing firing) => End(firing, "time up");

    private void End(Firing firing, string why)
    {
        lock (_gate)
        {
            firing.Ended = true;

            // Only remove the entry if it is still this timer. A stop followed by a fresh start for
            // the same shoebox must not have the old timer's late callback remove the new one.
            if (_active.TryGetValue(firing.ShoeboxId, out var current) && ReferenceEquals(current, firing))
            {
                _active.Remove(firing.ShoeboxId);
            }

            if (firing.Disposed) return;
            firing.Disposed = true;
        }

        firing.Ticker?.Dispose();
        firing.Expiry?.Dispose();

        // Reaches a run that has not started yet. A walk already under way is synchronous and
        // short, and finishes on its own.
        firing.Cancellation.Cancel();
        firing.Cancellation.Dispose();

        _logger?.LogInformation("Timed firing ended for {ShoeboxId} ({Why}) after {Runs} runs",
            firing.ShoeboxId, why, firing.Runs);
    }

    /// <summary>
    /// Refuses a diagram a timer should not hold: missing, too large, or one that would not run at
    /// all. Checked when it is sent, so the refusal is a 400 the sender sees rather than a
    /// lastError discovered later. The parser never throws; "would not run" means it found no
    /// services, or no entry point to start a request from.
    /// </summary>
    private static FiringOutcome? CheckDiagram(string? diagram)
    {
        if (string.IsNullOrWhiteSpace(diagram))
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid, "a diagram is required");
        }

        if (Encoding.UTF8.GetByteCount(diagram) > MaxDiagramBytes)
        {
            return FiringOutcome.Refuse(FiringRejection.TooLarge,
                $"the diagram is over {MaxDiagramBytes / 1024} KB, which is more than a timer will hold. Shrink it");
        }

        Graph graph;
        try
        {
            graph = MermaidParser.Parse(diagram);
        }
        catch (Exception ex)
        {
            return FiringOutcome.Refuse(FiringRejection.Invalid, $"the diagram could not be read: {ex.Message}");
        }

        if (graph.Pods.Count == 0 || graph.Entry is null)
        {
            var why = graph.Pods.Count == 0
                ? "no services were found in it"
                : "it has no entry point: every service is called by something, so there is nowhere to start";
            var notes = graph.Notes.Count > 0 ? $" Notes: {string.Join(" ", graph.Notes.Take(3))}" : string.Empty;
            return FiringOutcome.Refuse(FiringRejection.Invalid,
                $"the diagram would not run: {why}. POST /topology/parse shows what was read.{notes}");
        }

        return null;
    }

    private static FiringStatus StatusOf(Firing f, DateTimeOffset now) => new(
        Firing: !f.Ended,
        ShoeboxId: f.ShoeboxId,
        StartedAt: f.StartedAt,
        StopsAt: f.StopsAt,
        RemainingSeconds: Math.Max(0, (f.StopsAt - now).TotalSeconds),
        IntervalSeconds: f.Interval.TotalSeconds,
        Runs: f.Runs,
        Skipped: f.Skipped,
        LastTraceId: f.LastTraceId,
        LastError: f.LastError);

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds:0.###} seconds";

    private sealed class Firing(string shoeboxId, string source, string diagram, DateTimeOffset startedAt, DateTimeOffset stopsAt, TimeSpan interval)
    {
        public string ShoeboxId { get; } = shoeboxId;
        public string Source { get; } = source;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public TimeSpan Interval { get; } = interval;
        public CancellationTokenSource Cancellation { get; } = new();

        public string Diagram { get; set; } = diagram;
        public DateTimeOffset StopsAt { get; set; } = stopsAt;
        public ITimer? Ticker { get; set; }
        public ITimer? Expiry { get; set; }
        public bool InFlight { get; set; }
        public bool Ended { get; set; }
        public bool Disposed { get; set; }
        public int Runs { get; set; }
        public int Skipped { get; set; }
        public string? LastTraceId { get; set; }
        public string? LastError { get; set; }
    }
}
