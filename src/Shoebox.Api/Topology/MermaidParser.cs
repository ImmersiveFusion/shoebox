using System.Text.RegularExpressions;

namespace Shoebox.Api.Topology;

/// <summary>
/// Parses plain Mermaid flowchart text into a runnable graph.
///
/// There is no format to invent here. Mermaid is what every model writes fluently,
/// every developer already reads, and thousands of READMEs already contain. The
/// extension surface is three optional edge labels and two label suffixes, small
/// enough to document beside the paste box.
///
/// The parser is deliberately forgiving. Anything it does not understand becomes a
/// note and the run continues.
/// </summary>
public static partial class MermaidParser
{
    // one edge:  a[Label] -->|edge label| b[(Label)]
    [GeneratedRegex(@"^\s*(?<from>[A-Za-z0-9_]+)\s*(?<fromShape>\[\[.*?\]\]|\[\(.*?\)\]|\(\(.*?\)\)|\{\{.*?\}\}|\[.*?\])?\s*-{2,3}>\s*(?:\|(?<label>[^|]*)\|)?\s*(?<to>[A-Za-z0-9_]+)\s*(?<toShape>\[\[.*?\]\]|\[\(.*?\)\]|\(\(.*?\)\)|\{\{.*?\}\}|\[.*?\])?\s*$",
        RegexOptions.Compiled)]
    private static partial Regex EdgeLine();

    // a standalone node declaration:  worker[Worker x5]
    [GeneratedRegex(@"^\s*(?<id>[A-Za-z0-9_]+)\s*(?<shape>\[\[.*?\]\]|\[\(.*?\)\]|\(\(.*?\)\)|\{\{.*?\}\}|\[.*?\])\s*$",
        RegexOptions.Compiled)]
    private static partial Regex NodeLine();

    // class db broken
    [GeneratedRegex(@"^\s*class\s+(?<ids>[A-Za-z0-9_,\s]+?)\s+broken\s*$", RegexOptions.Compiled)]
    private static partial Regex ClassBrokenLine();

    // "Worker x5" -> replicas, "Worker #2" -> pinned instance
    [GeneratedRegex(@"\s+x(?<n>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ReplicaSuffix();

    [GeneratedRegex(@"\s+#(?<n>\d+)\s*$", RegexOptions.Compiled)]
    private static partial Regex InstanceSuffix();

    public static Graph Parse(string diagram)
    {
        var pods = new Dictionary<string, Pod>(StringComparer.Ordinal);
        var order = new List<string>();
        var calls = new List<Call>();
        var notes = new List<string>();
        var downedPods = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in (diagram ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("graph", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("classDef", StringComparison.Ordinal)) continue;

            var broken = ClassBrokenLine().Match(line);
            if (broken.Success)
            {
                foreach (var id in broken.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    downedPods.Add(id);
                }

                continue;
            }

            var edge = EdgeLine().Match(line);
            if (edge.Success)
            {
                Upsert(pods, order, edge.Groups["from"].Value, edge.Groups["fromShape"].Value);
                Upsert(pods, order, edge.Groups["to"].Value, edge.Groups["toShape"].Value);
                calls.Add(BuildCall(edge.Groups["from"].Value, edge.Groups["to"].Value, edge.Groups["label"].Value));
                continue;
            }

            var node = NodeLine().Match(line);
            if (node.Success)
            {
                Upsert(pods, order, node.Groups["id"].Value, node.Groups["shape"].Value);
                continue;
            }

            // Unknown but harmless. Recorded so the UI can say so, never thrown.
            notes.Add($"line not understood, ignored: {line}");
        }

        // A downed pod fails every call into it, from every caller. That is a
        // different trace shape from one broken dependency edge, and telling those
        // apart in a waterfall is a real skill worth teaching.
        if (downedPods.Count > 0)
        {
            for (var i = 0; i < calls.Count; i++)
            {
                if (downedPods.Contains(calls[i].ToId))
                {
                    calls[i] = calls[i] with { Broken = true, FailureReason = calls[i].FailureReason ?? "pod down" };
                }
            }
        }

        return new Graph
        {
            Pods = order.Select(id => pods[id]).ToList(),
            Calls = calls,
            Notes = notes,
        };
    }

    private static Call BuildCall(string from, string to, string? label)
    {
        var call = new Call(from, to);
        if (string.IsNullOrWhiteSpace(label)) return call;

        var text = label.Trim();

        // "phantom" - drawn, but nothing ever goes down this edge.
        if (text.StartsWith("phantom", StringComparison.OrdinalIgnoreCase))
        {
            return call with { Phantom = true };
        }

        if (!text.StartsWith("broken", StringComparison.OrdinalIgnoreCase)) return call;

        var rest = text[6..].Trim();
        string? reason = null;
        var instances = new List<int>();

        var colon = rest.IndexOf(':');
        if (colon >= 0)
        {
            reason = rest[(colon + 1)..].Trim();
            rest = rest[..colon].Trim();
        }

        // "on #3,#5"
        if (rest.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in rest[2..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part.TrimStart('#'), out var n)) instances.Add(n);
            }
        }

        return call with
        {
            Broken = true,
            BrokenInstances = instances,
            FailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        };
    }

    private static void Upsert(Dictionary<string, Pod> pods, List<string> order, string id, string? shape)
    {
        var hasShape = !string.IsNullOrEmpty(shape);
        if (pods.ContainsKey(id) && !hasShape) return;

        var (label, kind) = ReadShape(id, shape);
        var replicas = 1;
        int? pinned = null;

        var rep = ReplicaSuffix().Match(label);
        if (rep.Success)
        {
            replicas = Math.Max(1, int.Parse(rep.Groups["n"].Value));
            label = label[..rep.Index].Trim();
        }
        else
        {
            var inst = InstanceSuffix().Match(label);
            if (inst.Success)
            {
                pinned = int.Parse(inst.Groups["n"].Value);
                label = label[..inst.Index].Trim();
            }
        }

        var pod = new Pod(id, label, Slug(label), kind, replicas) { PinnedInstance = pinned };
        if (!pods.ContainsKey(id)) order.Add(id);
        pods[id] = pod;
    }

    private static (string Label, PodKind Kind) ReadShape(string id, string? shape)
    {
        if (string.IsNullOrEmpty(shape)) return (id, PodKind.Service);

        if (shape.StartsWith("[[", StringComparison.Ordinal)) return (shape[2..^2].Trim(), PodKind.Queue);
        if (shape.StartsWith("[(", StringComparison.Ordinal)) return (shape[2..^2].Trim(), PodKind.Datastore);
        if (shape.StartsWith("((", StringComparison.Ordinal)) return (shape[2..^2].Trim(), PodKind.Cache);
        if (shape.StartsWith("{{", StringComparison.Ordinal)) return (shape[2..^2].Trim(), PodKind.External);
        return (shape[1..^1].Trim(), PodKind.Service);
    }

    /// <summary>
    /// service.name comes from the label rather than the node id, because
    /// "api-gateway" in a service list is useful to somebody learning to read one
    /// and "gw" is not.
    /// </summary>
    private static string Slug(string label)
    {
        var chars = label.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? "service" : slug;
    }
}
