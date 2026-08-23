using System.Diagnostics;
using Shoebox.Api.Emit;
using Shoebox.Api.Topology;
using OpenTelemetry;

namespace Shoebox.Api.Run;

public sealed record RunResult(
    int RunIndex,
    string? TraceId,
    IReadOnlyList<string> ServedBy,
    int SpanCount,
    int FailedSpanCount,
    IReadOnlyList<string> Notes);

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

    /// <summary>
    /// Runs against the server's own export target.
    /// </summary>
    public RunResult Run(Graph graph, int runIndex, string? sandboxId) =>
        Run(graph, runIndex, sandboxId, scope: null);

    /// <summary>
    /// Runs against <paramref name="scope"/> when the caller brought its own export
    /// target, and against the server's otherwise. The caller owns the scope and
    /// disposing it is what flushes the spans.
    /// </summary>
    public RunResult Run(Graph graph, int runIndex, string? sandboxId, PodScope? scope)
    {
        var entry = graph.Entry;
        if (entry is null)
        {
            return new RunResult(runIndex, null, Array.Empty<string>(), 0, 0,
                new[] { "no entry point: every pod is called by something, so there is nowhere to start" });
        }

        if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            // sandbox.id rides Baggage onto every span, which is also a live
            // demonstration of baggage propagation inside a tool whose job is
            // teaching people to read telemetry.
            Baggage.SetBaggage(SandboxConstants.TagKey, sandboxId);
        }

        var state = new RunState(runIndex, scope);
        Visit(graph, entry, parent: null, state);

        return new RunResult(
            runIndex,
            state.RootTraceId,
            state.ServedBy,
            state.SpanCount,
            state.FailedSpanCount,
            graph.Notes);
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
        var source = Source(state, pod.ServiceName, instance);

        using var activity = source.StartActivity(
            SpanName(pod),
            pod.Kind == PodKind.Service && parent is null ? ActivityKind.Server : ActivityKind.Client,
            parent?.Context ?? default);

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
                EmitFailedCall(pod, target, call, activity, state, instance);
                continue;
            }

            Visit(graph, target, activity, state, depth + 1);
        }
    }

    private void EmitFailedCall(Pod from, Pod to, Call call, Activity? parent, RunState state, int instance)
    {
        var source = Source(state, from.ServiceName, instance);
        using var activity = source.StartActivity(
            $"{from.ServiceName} -> {to.ServiceName}",
            ActivityKind.Client,
            parent?.Context ?? default);

        if (activity is null) return;

        state.SpanCount++;
        state.FailedSpanCount++;
        var reason = call.FailureReason ?? "call failed";
        activity.SetStatus(ActivityStatusCode.Error, reason);
        activity.SetTag("error.type", reason);
        activity.SetTag(SandboxConstants.TagKey, Baggage.GetBaggage(SandboxConstants.TagKey));
        foreach (var (k, v) in SemanticTags(to)) activity.SetTag(k, v);
    }

    private ActivitySource Source(RunState state, string serviceName, int instance) =>
        state.Scope is { } scope ? scope.For(serviceName, instance) : _pool.For(serviceName, instance);

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

    private sealed class RunState(int runIndex, PodScope? scope)
    {
        public int RunIndex { get; } = runIndex;
        public PodScope? Scope { get; } = scope;
        public string? RootTraceId { get; set; }
        public List<string> ServedBy { get; } = new();
        public int SpanCount { get; set; }
        public int FailedSpanCount { get; set; }
        private readonly List<string> _notes = new();
        public void Note(string n) => _notes.Add(n);
    }
}
