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
}
