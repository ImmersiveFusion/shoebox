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
    /// <summary>
    /// Every arrow Mermaid offers, reduced to the one thing a run needs: a
    /// direction, and a label if there was one.
    ///
    /// This used to read <c>--&gt;</c> and nothing else, which was a quiet way to
    /// lose half a diagram. A real Azure reference architecture, written by a model
    /// and pasted unedited, came through as 6 of its 11 edges: the dotted
    /// <c>-. manages .-&gt;</c> went, the undirected <c>---</c> went, and every
    /// chained <c>a --&gt; b --&gt; c</c> went, because a chain is not one edge and the
    /// old pattern anchored to the end of the line. None of it errored. A run came
    /// back green for a system nobody drew, which is worse than a parse failure:
    /// a parse failure is visible.
    ///
    /// Alternation order matters. The labelled forms have to be tried before the
    /// bare ones, and the undirected form last, or <c>-- text --&gt;</c> matches as
    /// two dashes and leaves "text -->" to be read as a node.
    /// </summary>
    [GeneratedRegex(@"
        (?:
              -{2,}>                                 # -->  --->
            | -\.\s*(?<dotlabel>[^.|]*?)\s*\.-+>     # -. manages .->
            | -\.-+>                                 # -.->
            | ==\s*(?<eqlabel>[^=|]*?)\s*={2,}>      # == text ==>
            | ={2,}>                                 # ==>
            | --\s*(?<dashlabel>[^->|]*?)\s*-{2,}>   # -- text -->
            | (?<undirected>-{3,}|-{2,}(?!>))        # ---  (no arrowhead at all)
        )
        \s*(?:\|(?<label>[^|]*)\|)?                  # -->|broken: wrong table|
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LinkToken();

    /// <summary>
    /// One node as it appears either side of a link: an id, and optionally the
    /// shape that gives it meaning. The shape is optional because a diagram
    /// declares a node once and then refers to it by id.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<id>[A-Za-z0-9_]+)\s*(?<shape>\[\[.*?\]\]|\[\(.*?\)\]|\(\(.*?\)\)|\{\{.*?\}\}|\[.*?\])?\s*$",
        RegexOptions.Compiled)]
    private static partial Regex NodeToken();

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

    // subgraph HUBA["Hub network"] -- the id, so an edge drawn to the group can be named
    [GeneratedRegex(@"^\s*subgraph\s+(?<id>[A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SubgraphLine();

    /// <summary>Layout directives that are understood and deliberately not modelled.</summary>
    [GeneratedRegex(@"^\s*(subgraph\b|end\s*$|direction\b|style\b|linkStyle\b|click\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LayoutLine();

    public static Graph Parse(string diagram)
    {
        var pods = new Dictionary<string, Pod>(StringComparer.Ordinal);
        var order = new List<string>();
        var calls = new List<Call>();
        var notes = new List<string>();
        var downedPods = new HashSet<string>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in (diagram ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("graph", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("classDef", StringComparison.Ordinal)) continue;

            // Grouping and styling are for the renderer. Skipped in silence rather
            // than reported as "not understood", which is a lie that buries the
            // notes worth reading: a subgraph is understood perfectly well, it just
            // has no counterpart in a trace. The nodes declared inside one are read
            // normally, on their own lines.
            if (LayoutLine().IsMatch(line))
            {
                var group = SubgraphLine().Match(line);
                if (group.Success) groups.Add(group.Groups["id"].Value);
                continue;
            }

            var broken = ClassBrokenLine().Match(line);
            if (broken.Success)
            {
                foreach (var id in broken.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    downedPods.Add(id);
                }

                continue;
            }

            if (TryReadEdges(line, pods, order, calls, notes)) continue;

            var node = NodeLine().Match(line);
            if (node.Success)
            {
                Upsert(pods, order, node.Groups["id"].Value, node.Groups["shape"].Value);
                continue;
            }

            // Unknown but harmless. Recorded so the UI can say so, never thrown.
            notes.Add($"line not understood, ignored: {line}");
        }

        if (groups.Count > 0)
        {
            notes.Add("subgraphs ignored: grouping has no counterpart in a trace, so the nodes inside them were read on their own");

            // An edge drawn to the box rather than to something in it. Mermaid allows
            // it and means "to this group"; a run has nowhere to put that, so the
            // group becomes a service of its own. Said out loud, because a service
            // named after a network is the kind of thing a person spots instantly
            // and a parser never can.
            foreach (var id in order.Where(groups.Contains))
            {
                notes.Add($"{id} is a subgraph and something calls it, so it became a service of its own: point the arrow at a node inside it instead");
            }
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

    /// <summary>
    /// Reads every edge on one line, which may be a chain.
    ///
    /// <c>a --&gt; b --&gt; c</c> is two calls and one line. Splitting on the links and
    /// pairing what falls between them handles a chain of any length and a single
    /// edge identically, so there is one path rather than a special case that only
    /// the common shape goes down.
    ///
    /// All or nothing per line: if any token between two links is not a node, the
    /// line becomes a note and no half-read edge is admitted. A partly-read line is
    /// how a diagram silently becomes a different diagram.
    /// </summary>
    private static bool TryReadEdges(
        string line,
        Dictionary<string, Pod> pods,
        List<string> order,
        List<Call> calls,
        List<string> notes)
    {
        var links = LinkToken().Matches(line);
        if (links.Count == 0) return false;

        var tokens = new List<Match>(links.Count + 1);
        var position = 0;
        foreach (Match link in links)
        {
            var token = NodeToken().Match(line[position..link.Index]);
            if (!token.Success)
            {
                notes.Add($"line not understood, ignored: {line}");
                return true;
            }

            tokens.Add(token);
            position = link.Index + link.Length;
        }

        var last = NodeToken().Match(line[position..]);
        if (!last.Success)
        {
            notes.Add($"line not understood, ignored: {line}");
            return true;
        }

        tokens.Add(last);

        foreach (var token in tokens)
        {
            Upsert(pods, order, token.Groups["id"].Value, token.Groups["shape"].Value);
        }

        for (var i = 0; i < links.Count; i++)
        {
            var from = tokens[i].Groups["id"].Value;
            var to = tokens[i + 1].Groups["id"].Value;
            calls.Add(BuildCall(from, to, LabelOf(links[i])));

            // An undirected link names two things and not which one calls the
            // other. Read left to right, because that is the order somebody wrote
            // them in, and say so: half the time the request goes the other way
            // (a service calls its key vault, the key vault does not call it) and
            // a silent guess would be indistinguishable from a fact.
            if (links[i].Groups["undirected"].Success)
            {
                notes.Add($"undirected link read as {from} --> {to}: a call has a caller, so draw the arrow if it goes the other way");
            }
        }

        return true;
    }

    /// <summary>The label, wherever this arrow form happened to carry it.</summary>
    private static string? LabelOf(Match link)
    {
        foreach (var name in new[] { "label", "dotlabel", "eqlabel", "dashlabel" })
        {
            var group = link.Groups[name];
            if (group.Success && !string.IsNullOrWhiteSpace(group.Value)) return group.Value;
        }

        return null;
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

        if (shape.StartsWith("[[", StringComparison.Ordinal)) return (CleanLabel(shape[2..^2]), PodKind.Queue);
        if (shape.StartsWith("[(", StringComparison.Ordinal)) return (CleanLabel(shape[2..^2]), PodKind.Datastore);
        if (shape.StartsWith("((", StringComparison.Ordinal)) return (CleanLabel(shape[2..^2]), PodKind.Cache);
        if (shape.StartsWith("{{", StringComparison.Ordinal)) return (CleanLabel(shape[2..^2]), PodKind.External);
        return (CleanLabel(shape[1..^1]), PodKind.Service);
    }

    /// <summary>
    /// What the label says, minus the things it says to a renderer.
    ///
    /// Quotes are how Mermaid escapes a label containing punctuation, and a line
    /// break is how a diagram fits one on screen. Neither belongs in a service
    /// name. The break in particular used to arrive literally: a model writes
    /// <c>["Log Analytics\n(regional)"]</c> and the service turned up in the
    /// backend as <c>log-analytics-n-regional</c>, with the n from the escape.
    /// </summary>
    private static string CleanLabel(string text)
    {
        var cleaned = text
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (cleaned.Length > 1
            && ((cleaned[0] == '"' && cleaned[^1] == '"') || (cleaned[0] == '\'' && cleaned[^1] == '\'')))
        {
            cleaned = cleaned[1..^1];
        }

        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Trim();
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
