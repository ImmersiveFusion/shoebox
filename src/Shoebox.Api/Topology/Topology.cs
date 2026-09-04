namespace Shoebox.Api.Topology;

/// <summary>
/// What a Mermaid node shape means. The shapes people already reach for carry the
/// semantics, so nobody has to learn a convention: a cylinder is a database
/// because that is how everyone already draws one.
/// </summary>
public enum PodKind
{
    Service,
    Datastore,
    Queue,
    Cache,
    External,
}

public sealed record Pod(
    string Id,
    string Label,
    string ServiceName,
    PodKind Kind,
    int Replicas)
{
    /// <summary>
    /// Set when the label named a specific instance, as in "Worker #2". Null means
    /// the pod is an anonymous pool and any replica may serve a run.
    /// </summary>
    public int? PinnedInstance { get; init; }

    /// <summary>Default latency by shape. Overridable per edge later.</summary>
    public int DefaultLatencyMs => Kind switch
    {
        PodKind.Cache => 1,
        PodKind.Queue => 1,
        PodKind.Datastore => 8,
        PodKind.External => 200,
        _ => 15,
    };
}

public sealed record Call(string FromId, string ToId)
{
    /// <summary>True when every instance fails this call.</summary>
    public bool Broken { get; init; }

    /// <summary>
    /// Instances that fail this call when the pod is a pool, from "broken on #3".
    /// Empty with <see cref="Broken"/> true means all of them.
    /// </summary>
    public IReadOnlyList<int> BrokenInstances { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Text after the colon in "broken: connection refused". Becomes the span
    /// status description, which is what makes thirteen scenarios distinguishable
    /// when only three topologies exist.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Drawn, believed in, and never actually made, from "phantom".
    ///
    /// The call does not happen and the thing on the far end of it emits nothing,
    /// so it sits in the topology you drew and is absent from the trace. That gap
    /// is the whole lesson: the picture is a model, the trace is the system, and
    /// the first useful thing telemetry does is tell you where they differ.
    ///
    /// Not a failure. A failed call is a span with an error on it, which is
    /// evidence. This leaves no evidence at all, which is why it is harder to
    /// spot and worth teaching separately.
    /// </summary>
    public bool Phantom { get; init; }

    public bool FailsFor(int instance) =>
        Broken && (BrokenInstances.Count == 0 || BrokenInstances.Contains(instance));
}

public sealed class Graph
{
    public required IReadOnlyList<Pod> Pods { get; init; }

    public required IReadOnlyList<Call> Calls { get; init; }

    /// <summary>
    /// Parse problems that did not stop the graph being usable. An unknown shape or
    /// an unsupported directive is a note, never an error: a diagram somebody drew
    /// years ago for a design doc has to run, which is the property the whole paste
    /// box rests on.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public Pod? ById(string id) => Pods.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The entry point is the pod nothing calls. Ambiguity is rare, and taking the
    /// first such pod in document order keeps a run reproducible when it happens.
    /// </summary>
    public Pod? Entry
    {
        get
        {
            var called = Calls.Select(c => c.ToId).ToHashSet(StringComparer.Ordinal);
            return Pods.FirstOrDefault(p => !called.Contains(p.Id));
        }
    }

    public IEnumerable<Call> From(string podId) => Calls.Where(c => c.FromId == podId);

    /// <summary>
    /// Pods that can reach themselves, in document order.
    ///
    /// Drawing a pub/sub topic is the ordinary way to end up here: every service
    /// that publishes to a topic is usually also subscribed to it, so a faithful
    /// reading of a stock reference architecture puts two or three pods on a
    /// cycle without anybody meaning anything unusual by it.
    /// </summary>
    public IReadOnlyList<string> CyclicPods => _cyclicPods ??= FindCyclicPods();

    private IReadOnlyList<string>? _cyclicPods;

    /// <summary>
    /// What a cycle costs, said before the run rather than after it.
    ///
    /// The walk enumerates paths, not pods, so a cycle does not add a hop, it
    /// multiplies every hop downstream of it. One request through a diagram with
    /// a three-way cycle produced 23,428 spans and an hour-long trace on
    /// 2026-09-03, and <c>/topology/parse</c> — the endpoint whose whole job is to
    /// answer "is this safe to fire" — returned no notes at all. This is that
    /// missing sentence.
    /// </summary>
    public IReadOnlyList<string> CycleNotes
    {
        get
        {
            if (CyclicPods.Count == 0) return Array.Empty<string>();

            var named = string.Join(", ", CyclicPods.Select(id => ById(id)?.Label ?? id));
            return new[]
            {
                $"A request that reaches {named} can arrive back where it started. " +
                "A run still walks every path to its end — it just will not visit the same pod " +
                "twice on one causal path, the way a real request does not — so what comes back " +
                "is one honest resolution of this diagram rather than a loop or a truncation. " +
                "If the loop itself is what you wanted to model, the two directions through a " +
                "topic are usually two different events: draw them as two destinations and the " +
                "cycle goes away on its own.",
            };
        }
    }

    private IReadOnlyList<string> FindCyclicPods()
    {
        var result = new List<string>();
        foreach (var pod in Pods)
        {
            if (CanReachItself(pod.Id)) result.Add(pod.Id);
        }

        return result;
    }

    /// <summary>
    /// Plain reachability rather than Tarjan. These graphs are pasted by hand and
    /// run to tens of pods; the clarity is worth more than the asymptotics.
    /// </summary>
    private bool CanReachItself(string podId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        foreach (var call in From(podId)) pending.Push(call.ToId);

        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (string.Equals(id, podId, StringComparison.Ordinal)) return true;
            if (!seen.Add(id)) continue;

            foreach (var call in From(id)) pending.Push(call.ToId);
        }

        return false;
    }
}

/// <summary>
/// The ceiling on one run.
///
/// Depth alone never bounded anything. The walk expands paths, so a cycle at
/// branching factor two under a depth limit of 32 permits on the order of 2^32
/// of them: a limit that is arithmetically present and operationally absent. A
/// span budget bounds what the run actually costs — the thing that gets emitted,
/// stored, and drawn — and it holds whatever shape the diagram is.
/// </summary>
public static class RunLimits
{
    /// <summary>
    /// Comfortably above any honest diagram. The largest acyclic topology anyone
    /// has pasted runs to a few dozen spans, so this only ever fires on a walk
    /// that has stopped describing the picture.
    /// </summary>
    public const int MaxSpans = 500;

    /// <summary>
    /// Still here, and still worth keeping: it bounds a single path through a
    /// long chain, which the span budget does not distinguish from a wide one.
    /// </summary>
    public const int MaxDepth = 32;
}
