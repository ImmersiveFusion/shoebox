using System.Diagnostics;
using Shoebox.Api.Emit;
using Shoebox.Api.Topology;
using OpenTelemetry;

namespace Shoebox.Api.Run;

/// <summary>
/// One edge the request crossed, in the order it crossed it.
///
/// The client replays these to fly a dot along the diagram. It has to come from
/// here rather than be worked out in the browser: the server picks which replica
/// serves the run and decides which calls fail, so anything the client derived
/// could show a path the run did not take. In a tool for learning to read
/// telemetry that is not a rounding error, it is a lie.
/// </summary>
public sealed record Hop(string From, string To, bool Failed, int Ms);

public sealed record RunResult(
    int RunIndex,
    string? TraceId,
    IReadOnlyList<string> ServedBy,
    int SpanCount,
    int FailedSpanCount,
    IReadOnlyList<string> Notes,
    IReadOnlyList<Hop> Hops);

/// <summary>
/// Fires exactly one request through the graph and emits the spans it produces.
///
/// Nothing moves unless the user asks. That is what makes the lesson work: you
/// fired one request, you know its path, and you know what you broke. There is no
/// hunting through a burst for the interesting trace and no timing luck.
/// </summary>
public sealed class TopologyRunner
{
    private readonly PodTracerPool _pool;

    public TopologyRunner(PodTracerPool pool) => _pool = pool;

    public RunResult Run(Graph graph, int runIndex, string? sandboxId)
    {
        var entry = graph.Entry;
        if (entry is null)
        {
            return new RunResult(runIndex, null, Array.Empty<string>(), 0, 0,
                new[] { "no entry point: every pod is called by something, so there is nowhere to start" },
                Array.Empty<Hop>());
        }

        if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            // sandbox.id rides Baggage onto every span, which is also a live
            // demonstration of baggage propagation inside a tool whose job is
            // teaching people to read telemetry.
            Baggage.SetBaggage(SandboxConstants.TagKey, sandboxId);
        }

        // The simulated trace is its own trace, with the entry pod at the root.
        //
        // Without this it is a child of whatever ASP.NET has on Activity.Current
        // for the incoming request, which does two bad things. The trace comes out
        // rooted at a service called shoebox that is not in anybody's diagram. And
        // since nothing listens to that activity any more, it is not sampled, so
        // the parent-based sampler drops every simulated span underneath it and
        // the run records nothing at all.
        //
        // Detaching is what makes the diagram the whole state, in the telemetry
        // too and not just in the picture.
        var ambient = Activity.Current;
        Activity.Current = null;
        var state = new RunState(runIndex);
        try
        {
            Visit(graph, entry, parent: null, state);
        }
        finally
        {
            Activity.Current = ambient;
        }

        return new RunResult(
            runIndex,
            state.RootTraceId,
            state.ServedBy,
            state.SpanCount,
            state.FailedSpanCount,
            Notes(graph, state),
            state.Hops);
    }

    /// <summary>
    /// The diagram's own notes, plus the one thing only a run can tell you.
    /// </summary>
    private static IReadOnlyList<string> Notes(Graph graph, RunState state)
    {
        if (state.Phantoms.Count == 0) return graph.Notes;

        var named = string.Join(", ", state.Phantoms.Distinct());
        return graph.Notes
            .Append($"{named} never emitted a span of its own. Everything this trace knows about it, it learned from the services that called it.")
            .ToList();
    }

    private void Visit(Graph graph, Pod pod, Activity? parent, RunState state, int depth = 0)
    {
        // A cycle in a pasted diagram is somebody's real architecture, not a bug.
        // Bound the walk rather than refusing to run it.
        if (depth > 32)
        {
            state.Note("walk stopped at depth 32, the diagram contains a cycle");
            return;
        }

        var instance = SelectInstance(pod, state.RunIndex);
        var source = _pool.For(pod.ServiceName, instance);

        // Times come from the model, not from how long this loop took to run. A
        // walk of six pods finishes in microseconds, which exports a trace where
        // every span is a hairline and nothing can be read off it. Pod.Kind
        // already carries a plausible latency; this is what spends it.
        var start = state.Clock;
        using var activity = source.StartActivity(
            SpanName(pod),
            pod.Kind == PodKind.Service && parent is null ? ActivityKind.Server : ActivityKind.Client,
            parent?.Context ?? default,
            startTime: start);

        state.Clock = state.Clock.AddMilliseconds(pod.DefaultLatencyMs);

        if (activity is not null)
        {
            state.SpanCount++;
            state.RootTraceId ??= activity.TraceId.ToString();
            state.ServedBy.Add($"{pod.ServiceName}-{instance}");

            activity.SetTag(SandboxConstants.TagKey, Baggage.GetBaggage(SandboxConstants.TagKey));
            activity.SetTag("service.instance.id", $"{pod.ServiceName}-{instance}");
            foreach (var (k, v) in SemanticTags(pod)) activity.SetTag(k, v);
        }

        foreach (var call in graph.From(pod.Id))
        {
            var target = graph.ById(call.ToId);
            if (target is null) continue;

            if (call.FailsFor(instance))
            {
                state.Hop(pod.Id, target.Id, failed: true, ms: FailureMs);
                EmitFailedCall(pod, target, call, activity, state, instance);
                continue;
            }

            // Drawn but never called. No hop, no span, no trace of it at all,
            // which is exactly the point: nothing arrives to tell you it is
            // missing, so the only way to notice is to compare the two panels.
            if (call.Phantom)
            {
                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                EmitPhantomCall(pod, target, activity, state, instance);
                continue;
            }

            state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
            Visit(graph, target, activity, state, depth + 1);
        }

        activity?.SetEndTime(state.Clock.UtcDateTime);
    }

    /// <summary>
    /// A call to something that never speaks for itself.
    ///
    /// The caller emits its client span like any other call, so the service is
    /// named all over the trace: peer.service, the span name, the semantic
    /// attributes for whatever kind of thing it is. What never arrives is a span
    /// from the service itself, and nothing downstream of it happens either.
    ///
    /// That is what a phantom is. Anything building a service map out of traces
    /// will draw this node, because other people's spans insist it exists, and it
    /// has never emitted a byte of telemetry in its life. Nothing is marked red
    /// and no call failed: every span in the trace is a success. The only
    /// evidence is an absence, which is why it is the hardest of these to see and
    /// the one most worth having an example for.
    /// </summary>
    private void EmitPhantomCall(Pod from, Pod to, Activity? parent, RunState state, int instance)
    {
        var source = _pool.For(from.ServiceName, instance);
        using var activity = source.StartActivity(
            $"{from.ServiceName} -> {to.ServiceName}",
            ActivityKind.Client,
            parent?.Context ?? default,
            startTime: state.Clock);

        state.Clock = state.Clock.AddMilliseconds(to.DefaultLatencyMs);
        state.Phantoms.Add(to.ServiceName);
        if (activity is null) return;

        activity.SetEndTime(state.Clock.UtcDateTime);
        state.SpanCount++;

        activity.SetTag(SandboxConstants.TagKey, Baggage.GetBaggage(SandboxConstants.TagKey));
        activity.SetTag("peer.service", to.ServiceName);
        foreach (var (k, v) in SemanticTags(to)) activity.SetTag(k, v);
    }

    /// <summary>
    /// A refused call comes back fast. That is the tell, and it is worth showing:
    /// the failing hop is usually the shortest bar in the waterfall, not the
    /// longest, which is the opposite of what people expect to look for.
    /// </summary>
    private const int FailureMs = 2;

    private void EmitFailedCall(Pod from, Pod to, Call call, Activity? parent, RunState state, int instance)
    {
        var source = _pool.For(from.ServiceName, instance);
        using var activity = source.StartActivity(
            $"{from.ServiceName} -> {to.ServiceName}",
            ActivityKind.Client,
            parent?.Context ?? default);

        if (activity is null) return;

        state.Clock = state.Clock.AddMilliseconds(FailureMs);
        activity.SetEndTime(state.Clock.UtcDateTime);

        state.SpanCount++;
        state.FailedSpanCount++;
        var reason = call.FailureReason ?? "call failed";
        activity.SetStatus(ActivityStatusCode.Error, reason);
        activity.SetTag("error.type", reason);
        activity.SetTag(SandboxConstants.TagKey, Baggage.GetBaggage(SandboxConstants.TagKey));
        foreach (var (k, v) in SemanticTags(to)) activity.SetTag(k, v);
    }

    /// <summary>
    /// Which replica serves this run. Deterministic round robin, never random.
    ///
    /// A link is meant to be a runnable repro, and random selection breaks that
    /// promise quietly: two people open the same URL, fire once, and get different
    /// traces with no way to tell why. Round robin also turns "broken on #3" into a
    /// walk rather than a coin flip, so firing five times teaches what one-in-five
    /// failure actually looks like.
    /// </summary>
    private static int SelectInstance(Pod pod, int runIndex)
    {
        if (pod.PinnedInstance is { } pinned) return pinned;
        if (pod.Replicas <= 1) return 1;
        return ((runIndex - 1) % pod.Replicas + pod.Replicas) % pod.Replicas + 1;
    }

    private static string SpanName(Pod pod) => pod.Kind switch
    {
        PodKind.Datastore => $"SELECT {pod.ServiceName}",
        PodKind.Cache => $"GET {pod.ServiceName}",
        PodKind.Queue => $"{pod.ServiceName} publish",
        PodKind.External => $"POST {pod.ServiceName}",
        _ => $"{pod.ServiceName} handle",
    };

    /// <summary>
    /// The shape somebody already drew decides the attributes. Nobody has to learn
    /// a convention to get semantically correct telemetry out.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, object?>> SemanticTags(Pod pod)
    {
        yield return new("shoebox.pod.kind", pod.Kind.ToString().ToLowerInvariant());

        switch (pod.Kind)
        {
            case PodKind.Datastore:
                yield return new("db.system.name", "postgresql");
                yield return new("db.query.text", $"SELECT * FROM {pod.ServiceName}");
                break;
            case PodKind.Cache:
                yield return new("db.system.name", "redis");
                yield return new("db.operation.name", "GET");
                break;
            case PodKind.Queue:
                yield return new("messaging.system", "rabbitmq");
                yield return new("messaging.destination.name", pod.ServiceName);
                break;
            case PodKind.External:
                yield return new("http.request.method", "POST");
                yield return new("server.address", $"{pod.ServiceName}.example.com");
                break;
            default:
                yield return new("http.request.method", "GET");
                yield return new("http.route", $"/{pod.ServiceName}");
                break;
        }
    }

    private sealed class RunState(int runIndex)
    {
        public int RunIndex { get; } = runIndex;

        /// <summary>Modelled time, not wall clock. Advanced by each pod's latency.</summary>
        public DateTimeOffset Clock { get; set; } = DateTimeOffset.UtcNow;

        public List<Hop> Hops { get; } = new();

        /// <summary>Called, named all over the trace, and never heard from.</summary>
        public List<string> Phantoms { get; } = new();
        public void Hop(string from, string to, bool failed, int ms) =>
            Hops.Add(new Hop(from, to, failed, ms));
        public string? RootTraceId { get; set; }
        public List<string> ServedBy { get; } = new();
        public int SpanCount { get; set; }
        public int FailedSpanCount { get; set; }
        private readonly List<string> _notes = new();
        public void Note(string n) => _notes.Add(n);
    }
}
