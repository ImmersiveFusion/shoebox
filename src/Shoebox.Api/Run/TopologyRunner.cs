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
        var notes = graph.Notes.Concat(state.Notes);

        if (state.Phantoms.Count > 0)
        {
            var named = string.Join(", ", state.Phantoms.Distinct());
            notes = notes.Append(
                $"Nothing consumed what was published. {named} should have and never ran, so this trace has a publish with no receive and no span anywhere carries its name. An absence is the only evidence a phantom leaves.");
        }

        return notes.ToList();
    }

    private void Visit(Graph graph, Pod pod, Activity? parent, RunState state, int depth = 0, (Pod Queue, string MessageId)? via = null)
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
        // A pod reached through a queue is a consumer, and OpenTelemetry has a
        // span kind and a name shape for exactly that. Getting this wrong is not
        // cosmetic: anything inferring topology from messaging looks for a
        // producer and a consumer on the same destination, and a CLIENT span
        // carrying no operation type pairs with nothing.
        var kind = via is not null
            ? ActivityKind.Consumer
            : pod.Kind == PodKind.Service && parent is null ? ActivityKind.Server : ActivityKind.Client;

        var start = state.Clock;
        using var activity = source.StartActivity(
            via is { } d ? $"process {d.Queue.ServiceName}" : SpanName(pod),
            kind,
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
            // A consumer is a messaging span and only a messaging span. It was
            // also carrying http.request.method, which says this service was
            // reached over HTTP when it was reached off a queue. Anything reading
            // the telemetry to work out how a service is called gets two
            // contradictory answers, and the transport is the thing it is trying
            // to establish.
            if (via is { } delivery)
            {
                activity.SetTag("shoebox.pod.kind", pod.Kind.ToString().ToLowerInvariant());
                foreach (var (k, v) in MessagingTags(delivery.Queue, "process", "process", delivery.MessageId))
                {
                    activity.SetTag(k, v);
                }
            }
            else
            {
                foreach (var (k, v) in SemanticTags(pod, kind)) activity.SetTag(k, v);
            }
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
            // A dead consumer, which is what Snowglobe means by a phantom:
            // "services you didn't know you had: dead consumers, so the platform
            // infers the missing services from the topology".
            //
            // Nothing here emits and nothing emits about it. What makes it
            // inferable is on the other side of the queue: the producer publishes
            // normally, with messaging.destination.name on its span, and no
            // receive ever correlates to it. A backend can tell something ought to
            // be consuming that destination. The trace can only show you that it
            // never did.
            //
            // Which is why skipping is the whole implementation. An earlier
            // version had the caller emit a client span naming the phantom, and
            // that is precisely backwards: the name appearing anywhere is what a
            // phantom is defined by not doing.
            // Only reachable on a direct edge, where it is meaningless: a
            // synchronous callee that is not there refuses the connection, and a
            // refused connection is an error span, which is evidence. Phantoms
            // are the absence of evidence, so they only exist behind a queue.
            // Handled properly in PublishAndDeliver.
            if (call.Phantom)
            {
                state.Note($"phantom on a direct call to {target.ServiceName} is ignored: a service that is not there would refuse the connection and the trace would show the error. Put it behind a queue.");
                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                Visit(graph, target, activity, state, depth + 1);
                continue;
            }

            // A queue is a destination, not a service. The publish belongs to
            // whoever published and the receive to whoever consumed; nothing
            // emits on behalf of the queue itself, because in a real system
            // nothing does.
            // Producer semantics only when the diagram models the far side.
            //
            // A queue drawn with nothing after it is the end of what was drawn,
            // not a statement that nothing consumes it. Publishing to it with no
            // receive says the second thing, and anything watching for unconsumed
            // destinations then reports every terminal queue in every diagram as a
            // phantom. That is how RabbitMQ became one in an example whose lesson
            // was about a broken worker.
            //
            // "Nothing consumes this" is a claim, and the only thing entitled to
            // make it is the phantom marker, where somebody said so on purpose.
            var consumers = graph.From(target.Id).ToList();
            if (target.Kind == PodKind.Queue && consumers.Count > 0)
            {
                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                PublishAndDeliver(graph, pod, target, consumers, activity, state, depth, instance);
                continue;
            }

            state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
            Visit(graph, target, activity, state, depth + 1);
        }

        activity?.SetEndTime(state.Clock.UtcDateTime);
    }

    /// <summary>
    /// The publish, and whatever picks it up.
    ///
    /// The producer emits a PRODUCER span naming the destination, and each
    /// consumer emits its own CONSUMER span naming the same one. That pairing is
    /// what makes a queue legible to anything reading the telemetry, and it is
    /// what a phantom breaks: the publish happens, no receive ever correlates to
    /// it, and the missing consumer can be inferred precisely because the
    /// destination is on both halves when things are working.
    /// </summary>
    private void PublishAndDeliver(Graph graph, Pod producer, Pod queue, IReadOnlyList<Call> consumers, Activity? parent, RunState state, int depth, int instance)
    {
        // Deterministic: the same diagram and run index produce the same id, so a
        // shared link is still a runnable repro.
        var messageId = $"{queue.ServiceName}-{state.RunIndex}";

        var source = _pool.For(producer.ServiceName, instance);
        using var publish = source.StartActivity(
            $"publish {queue.ServiceName}",
            ActivityKind.Producer,
            parent?.Context ?? default,
            startTime: state.Clock);

        state.Clock = state.Clock.AddMilliseconds(queue.DefaultLatencyMs);

        if (publish is not null)
        {
            publish.SetEndTime(state.Clock.UtcDateTime);
            state.SpanCount++;
            state.RootTraceId ??= publish.TraceId.ToString();
            publish.SetTag(SandboxConstants.TagKey, Baggage.GetBaggage(SandboxConstants.TagKey));
            publish.SetTag("service.instance.id", $"{producer.ServiceName}-{instance}");
            foreach (var (k, v) in MessagingTags(queue, "publish", "send", messageId)) publish.SetTag(k, v);
        }

        foreach (var call in consumers)
        {
            var consumer = graph.ById(call.ToId);
            if (consumer is null) continue;

            if (call.FailsFor(SelectInstance(consumer, state.RunIndex)))
            {
                state.Hop(queue.Id, consumer.Id, failed: true, ms: FailureMs);
                EmitFailedCall(queue, consumer, call, publish, state, instance);
                continue;
            }

            if (call.Phantom)
            {
                state.Phantoms.Add(consumer.ServiceName);
                continue;
            }

            state.Hop(queue.Id, consumer.Id, failed: false, ms: consumer.DefaultLatencyMs);
            Visit(graph, consumer, publish, state, depth + 1, via: (queue, messageId));
        }
    }

    /// <summary>
    /// The OpenTelemetry messaging attributes, current spelling. operation.type is
    /// the one that pairs a publish with a receive, and it is the one Shoebox was
    /// missing entirely.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, object?>> MessagingTags(
        Pod queue, string operationName, string operationType, string messageId)
    {
        yield return new("messaging.system", "rabbitmq");
        yield return new("messaging.destination.name", queue.ServiceName);

        // Deprecated since semconv 1.17, emitted anyway and deliberately.
        //
        // Dual-emission is the documented way to cross a rename, and this rename
        // is not finished in the wild: real consumers still read the old key, so a
        // producer that only speaks the new one pairs with nothing and its queue
        // looks unconsumed to anything that has not caught up. Emitting both costs
        // one attribute and is correct for either vintage. It comes out when the
        // readers have moved.
        yield return new("messaging.destination", queue.ServiceName);

        // operation.name is a free string and takes the system's own word.
        // operation.type is an enumeration and takes exactly one of create, send,
        // receive, process, settle. "publish" was in there and is not a member of
        // it: an invented value on the one attribute a conformant backend reads to
        // decide whether a span is a producer or a consumer, which is why nothing
        // could pair the two halves of a queue.
        yield return new("messaging.operation.name", operationName);
        yield return new("messaging.operation.type", operationType);

        // Without an id there is nothing to correlate a receive to a publish at
        // the message level, which is the correlation the spec actually defines.
        // Deterministic, because a shared link has to replay identically.
        yield return new("messaging.message.id", messageId);
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
        foreach (var (k, v) in SemanticTags(to, ActivityKind.Client)) activity.SetTag(k, v);
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
    private static IEnumerable<KeyValuePair<string, object?>> SemanticTags(Pod pod, ActivityKind kind)
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
                // server.port and url.full are Required on HTTP client spans, not
                // optional extras, and both were missing.
                yield return new("http.request.method", "POST");
                yield return new("server.address", $"{pod.ServiceName}.example.com");
                yield return new("server.port", 443);
                yield return new("url.full", $"https://{pod.ServiceName}.example.com/{pod.ServiceName}");
                break;
            default:
                yield return new("http.request.method", "GET");

                // http.route is a SERVER-span attribute and appears nowhere in the
                // client table, so it is conditional on kind.
                //
                // And nothing else. A previous attempt put server.address here as
                // "{service}.internal", which invented a hostname that nothing
                // ever emits under, so every internal service in the diagram was
                // inferred as a phantom peer. server.* describes the far end of an
                // outbound call; this span is the pod itself, and the pod is not
                // its own peer. Naming a host that does not exist is the same
                // fabrication as naming an attribute that does not exist.
                if (kind == ActivityKind.Server)
                {
                    yield return new("http.route", $"/{pod.ServiceName}");
                }

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

        /// <summary>
        /// These were being collected and thrown away. The cycle warning has been
        /// written by the walk and dropped on the floor since it was added, so a
        /// diagram that loops has been silently truncating at depth 32 with
        /// nothing said about it.
        /// </summary>
        public IReadOnlyList<string> Notes => _notes;
    }
}
