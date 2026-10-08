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

    /// <summary>
    /// Set when the label named a revision, as in "Orders API x3 rev2": a redeploy
    /// that replaces every pod of the service. Null means no marker, which names pods
    /// exactly as revision 0 would.
    ///
    /// Pod names are stable on purpose: scale x2 to x4 and back and the first two
    /// pods keep their names, as pods that survived a scale-out would. A redeploy is
    /// the opposite case, where every pod of the service is replaced, and bumping the
    /// revision is how a diagram says so.
    /// </summary>
    public int? Revision { get; init; }

    /// <summary>The service.instance.id of the pod at one position, 1-based.</summary>
    public string InstanceId(int instance) => InstanceIdOf(ServiceName, instance, Revision);

    /// <summary>Every pod name this node can serve from, in position order.</summary>
    public IReadOnlyList<string> InstanceIds =>
        PinnedInstance is { } pinned
            ? new[] { InstanceId(pinned) }
            : InstanceIdsOf(ServiceName, Math.Clamp(Replicas, 1, UniqueNamePositions), Revision);

    /// <summary>
    /// A Deployment's pod name: <c>{service}-{templateHash}-{suffix}</c>, as in
    /// <c>orders-api-9fgkfp96z4-bh4ks</c>.
    ///
    /// A backend that groups pods into services is tested against names that look
    /// like the ones it will meet, and those are never <c>orders-api-1</c>. The
    /// template hash is the same for every pod of one service at one revision and
    /// changes with the revision, the way a ReplicaSet's does when its pod template
    /// changes; the suffix is per pod. Both are SHA-256 based rather than
    /// <c>GetHashCode</c>, which differs between processes, so a shared link names
    /// the same pods everywhere. The position <c>n</c> chooses the pod and is not
    /// part of its name, so <c>#2</c> and <c>broken on #3</c> mean what they did.
    /// </summary>
    public static string InstanceIdOf(string serviceName, int instance, int? revision)
    {
        if (instance is >= 1 and <= UniqueNamePositions)
        {
            return InstanceIdsOf(serviceName, instance, revision)[instance - 1];
        }

        // Outside the guaranteed range: named by its first draw alone, so the cost
        // of one name never grows with a replica count somebody typed.
        var rev = revision ?? 0;
        return $"{serviceName}-{TemplateHash(serviceName, rev)}-{SuffixDraw(serviceName, rev, instance, 0)}";
    }

    /// <summary>
    /// The names of positions 1..count of one service at one revision, where count
    /// is at most <see cref="UniqueNamePositions"/>.
    /// </summary>
    public static IReadOnlyList<string> InstanceIdsOf(string serviceName, int count, int? revision)
    {
        var rev = revision ?? 0;
        var hash = TemplateHash(serviceName, rev);
        return AssignSuffixes(Math.Min(count, UniqueNamePositions), (n, attempt) => SuffixDraw(serviceName, rev, n, attempt))
            .Select(suffix => $"{serviceName}-{hash}-{suffix}")
            .ToArray();
    }

    /// <summary>
    /// Positions up to this one are guaranteed distinct names, and
    /// <c>/topology/parse</c> lists at most this many per node. Naming position n
    /// costs n hashes, and n comes from a diagram anybody can paste, so it is
    /// bounded; far above any honest diagram, which runs to a few dozen pods.
    /// </summary>
    public const int UniqueNamePositions = 256;

    private static string SuffixDraw(string serviceName, int revision, int position, int attempt) => SafeEncode(
        attempt == 0 ? $"{serviceName}|{revision}|{position}" : $"{serviceName}|{revision}|{position}|{attempt}",
        SuffixLength);

    /// <summary>
    /// Suffixes for positions 1..count, unique by construction.
    ///
    /// Five characters from 27 is about 14 million names, so two pods of one
    /// service can draw the same one, and with the pool keyed on the name they
    /// would silently become one pod. Kubernetes retries a taken name; this does the
    /// same, deterministically: positions are assigned in order, and a suffix taken
    /// by a lower position is redrawn with an attempt counter until it is free.
    /// Lower positions never depend on higher ones, so scaling up keeps every
    /// existing name. <paramref name="candidate"/> is (position, attempt) to suffix,
    /// and is a parameter so a test can force a collision.
    /// </summary>
    public static IReadOnlyList<string> AssignSuffixes(int count, Func<int, int, string> candidate)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var suffixes = new string[count];
        for (var n = 1; n <= count; n++)
        {
            var attempt = 0;
            var suffix = candidate(n, attempt);
            while (!taken.Add(suffix)) suffix = candidate(n, ++attempt);
            suffixes[n - 1] = suffix;
        }

        return suffixes;
    }

    /// <summary>The ReplicaSet part of every pod name for one service at one revision.</summary>
    public static string TemplateHash(string serviceName, int revision) =>
        SafeEncode($"{serviceName}|{revision}", TemplateHashLength);

    public const int TemplateHashLength = 10;

    public const int SuffixLength = 5;

    /// <summary>
    /// The alphabet Kubernetes uses for generated names (rand.SafeEncodeString in
    /// k8s.io/apimachinery): no vowels, so no words, and no 0, 1 or 3, so nothing
    /// that reads as a letter.
    /// </summary>
    public const string SafeAlphabet = "bcdfghjklmnpqrstvwxz2456789";

    private static string SafeEncode(string input, int length)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = SafeAlphabet[digest[i] % SafeAlphabet.Length];
        return new string(chars);
    }

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
