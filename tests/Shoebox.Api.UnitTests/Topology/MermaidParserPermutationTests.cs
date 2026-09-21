using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Topology
{
    /// <summary>
    /// The parser's bug history is four instances of one bug. Arrows that were not
    /// <c>--&gt;</c>. Chains. A <c>\n</c> escape inside a label. Hyphenated ids. Every
    /// one was ordinary Mermaid, every one was silently dropped, and every one was
    /// found by somebody pasting a real diagram rather than by a test.
    ///
    /// Example-based tests caught none of them, because each example was written by
    /// the same person who wrote the parser and shared its blind spots exactly. So
    /// these take the cross product instead: every arrow form against every id style
    /// against every shape, and then that whole grid again through the character
    /// substitutions a language model actually emits.
    ///
    /// The invariant is the same in all of them and it is the one that matters. Not
    /// "the graph is correct" -- "nothing was silently dropped". A parse that loses
    /// half a diagram and says so is a bug worth filing; one that loses half a diagram
    /// and returns 200 is the one that burned a rate limit.
    /// </summary>
    [TestFixture]
    public class MermaidParserPermutationTests
    {
        /// <summary>Every arrow the parser claims to read, including the labelled and undirected forms.</summary>
        public static readonly string[] Arrows =
        {
            "-->", "--->", "---->",
            "-.->", "-. manages .->",
            "==>", "=====>", "== uses ==>",
            "-- calls -->",
            "---", "----",
        };

        /// <summary>Id spellings a model reaches for unprompted.</summary>
        public static readonly string[] Ids =
        {
            "a", "api", "order_service", "order-service",
            "gw1", "gw-1", "a-b-c", "_internal", "svc2db",
        };

        public static readonly string[] Shapes =
        {
            "", "[Order Service]", "[[Job Queue]]", "[(Postgres)]", "((Redis))", "{{Stripe}}",
        };

        [Test, Combinatorial]
        public void Every_Arrow_Joins_Every_Pair_Of_Ids(
            [ValueSource(nameof(Arrows))] string arrow,
            [ValueSource(nameof(Ids))] string from,
            [ValueSource(nameof(Ids))] string to)
        {
            var graph = MermaidParser.Parse($"flowchart LR\n  {from} {arrow} {to}");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.Calls.Should().ContainSingle();
            graph.Calls[0].FromId.Should().Be(from);
            graph.Calls[0].ToId.Should().Be(to);
            graph.Pods.Select(p => p.Id).Should().BeEquivalentTo(
                from == to ? new[] { from } : new[] { from, to });
        }

        [Test, Combinatorial]
        public void Every_Shape_Attaches_To_Every_Id_Style(
            [ValueSource(nameof(Ids))] string id,
            [ValueSource(nameof(Shapes))] string shape)
        {
            var graph = MermaidParser.Parse($"flowchart LR\n  entry[Entry] --> {id}{shape}");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.ById(id).Should().NotBeNull();
            graph.ById(id)!.Kind.Should().Be(shape switch
            {
                "[[Job Queue]]" => PodKind.Queue,
                "[(Postgres)]" => PodKind.Datastore,
                "((Redis))" => PodKind.Cache,
                "{{Stripe}}" => PodKind.External,
                _ => PodKind.Service,
            });
        }

        /// <summary>
        /// A substitution a diagram picks up between the model writing it and the
        /// parser reading it. None of these are hypothetical; they are what arrives.
        /// </summary>
        public sealed record Mangling(string Name, Func<string, string> Apply)
        {
            public override string ToString() => Name;
        }

        public static readonly Mangling[] Manglings =
        {
            new("clean ascii", d => d),

            // Autocorrect, and every model that learned to write from prose.
            new("em dash for --", d => d.Replace("--", "—")),
            new("en dash inside ids", d => Regex.Replace(d, "(?<=[A-Za-z0-9])-(?=[A-Za-z0-9])", "–")),
            new("unicode arrow", d => d.Replace("-->", "→")),

            // A paste that went through a rich text field on the way.
            new("non-breaking spaces", d => d.Replace(" ", " ")),
            new("bom and zero-width spaces", d => "﻿" + d.Replace("-", "​-")),
            new("smart quoted labels", d => Regex.Replace(d, @"\[([A-Za-z][A-Za-z ]*)\]", "[“$1”]")),
        };

        /// <summary>
        /// The golden invariant: a mangled diagram and its clean original are the same
        /// graph, down to the notes. Comparing whole descriptions rather than picked
        /// fields is deliberate -- a substitution that quietly changed a service name
        /// or invented a note would pass a narrower assertion.
        /// </summary>
        [Test, Combinatorial]
        public void A_Mangled_Diagram_Parses_As_Its_Clean_Original(
            [ValueSource(nameof(Arrows))] string arrow,
            [ValueSource(nameof(Manglings))] Mangling mangling)
        {
            var clean = $"flowchart LR\n  order-service[Order Service] {arrow} db-primary[(Postgres)]";

            Describe(MermaidParser.Parse(mangling.Apply(clean)))
                .Should().Be(Describe(MermaidParser.Parse(clean)));
        }

        [Test, Combinatorial]
        public void Mangling_Survives_Every_Id_Style(
            [ValueSource(nameof(Ids))] string id,
            [ValueSource(nameof(Manglings))] Mangling mangling)
        {
            var clean = $"flowchart LR\n  entry[Entry] --> {id}[Downstream]";

            Describe(MermaidParser.Parse(mangling.Apply(clean)))
                .Should().Be(Describe(MermaidParser.Parse(clean)));
        }

        /// <summary>
        /// The extensions ride on label text, so they have to survive the fold too. An
        /// em dash inside a label is prose and must be left exactly as written.
        /// </summary>
        [Test]
        public void Folding_Leaves_Label_Prose_Alone()
        {
            var graph = MermaidParser.Parse(
                "flowchart LR\n  api[“Orders — the main one”] -->|broken: timeout — gave up| db-primary[(Postgres)]");

            graph.Notes.Should().NotContain(n => n.Contains("not understood"));
            graph.ById("api")!.Label.Should().Be("Orders — the main one");
            graph.Calls.Should().ContainSingle();
            graph.Calls[0].Broken.Should().BeTrue();
            graph.Calls[0].FailureReason.Should().Be("timeout — gave up");
        }

        private static string Describe(Graph graph) => string.Join("\n",
            graph.Pods.Select(p => $"pod {p.Id} {p.Kind} '{p.Label}' {p.ServiceName} x{p.Replicas} #{p.PinnedInstance}")
                .Concat(graph.Calls.Select(c =>
                    $"call {c.FromId}->{c.ToId} broken={c.Broken} phantom={c.Phantom} reason={c.FailureReason}"))
                .Concat(graph.Notes.Select(n => $"note {n}")));
    }
}
