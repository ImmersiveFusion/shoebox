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

/// <summary>
/// An edge that is in the diagram and was not crossed, and why.
///
/// Same argument as <see cref="Hop"/>, pointed the other way. A client can draw the
/// path a request took because the server sends it; it cannot draw the difference
/// between the diagram and the run unless the server sends that too. Left to prose
/// in <c>notes</c>, the picture shows every arrow identically and a run that walked
/// two thirds of the diagram looks exactly like one that walked all of it.
///
/// <c>From</c> and <c>To</c> are pod ids, matching <see cref="Hop"/>, so a renderer
/// can key straight onto the nodes it already drew.
/// </summary>
public sealed record NotTaken(string From, string To, string Reason);

public sealed record RunResult(
    int RunIndex,
    string? TraceId,
    IReadOnlyList<string> ServedBy,
    int SpanCount,
    int FailedSpanCount,
    IReadOnlyList<string> Notes,
    IReadOnlyList<Hop> Hops,
    IReadOnlyList<NotTaken> NotTaken);

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

    public RunResult Run(Graph graph, int runIndex, string? shoeboxId)
    {
        var entry = graph.Entry;
        if (entry is null)
        {
            return new RunResult(runIndex, null, Array.Empty<string>(), 0, 0,
                new[] { "no entry point: every pod is called by something, so there is nowhere to start" },
                Array.Empty<Hop>(), Array.Empty<NotTaken>());
        }

        if (!string.IsNullOrWhiteSpace(shoeboxId))
        {
            // shoebox.id rides Baggage onto every span, which is also a live
            // demonstration of baggage propagation inside a tool whose job is
            // teaching people to read telemetry.
            Baggage.SetBaggage(ShoeboxConstants.TagKey, shoeboxId);
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
            // The entry pod is on the path before the walk starts, or a diagram
            // that calls back to its own front door loops on the first lap.
            state.Enter(entry.Id);
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
            state.Hops,
            NotTakenEdges(state));
    }

    /// <summary>
    /// Edges the request never crossed, anywhere in the run.
    ///
    /// Declining an edge is per path, so the same arrow can be refused on one path
    /// and crossed on another: <c>ui -> accounting -> topic -> accounting</c> turns
    /// back, and <c>ui -> order -> topic -> accounting</c> does not. Only an edge
    /// that was refused every time it came up is one the picture should show
    /// differently — anything else would grey out an arrow the request did cross,
    /// which is the exact class of lie <see cref="Hop"/> exists to prevent.
    /// </summary>
    private static IReadOnlyList<NotTaken> NotTakenEdges(RunState state)
    {
        var crossed = state.Hops.Select(h => $"{h.From}->{h.To}").ToHashSet(StringComparer.Ordinal);

        return state.DeclinedEdges
            .Where(e => !crossed.Contains($"{e.From}->{e.To}"))
            .ToList();
    }

    /// <summary>
    /// The diagram's own notes, plus the one thing only a run can tell you.
    /// </summary>
    private static IReadOnlyList<string> Notes(Graph graph, RunState state)
    {
        var notes = graph.Notes.Concat(graph.CycleNotes).Concat(state.Notes);

        if (state.DeclinedEdges.Count > 0)
        {
            // Ids are what the structured NotTaken carries, because a renderer keys
            // on them. A sentence read aloud to a person wants the service names.
            var named = string.Join(", ", state.DeclinedEdges.Select(e =>
                $"{graph.ById(e.From)?.ServiceName ?? e.From} -> {graph.ById(e.To)?.ServiceName ?? e.To}"));
            notes = notes.Append(
                $"The request arrived back at something it had already passed through, so it did not go round again: {named}. " +
                "That is one request resolved against a diagram that contains a cycle, not a truncated one — " +
                "every path was walked to its end. A service reached twice by two different callers still runs twice; " +
                "what never happens is the same service twice on one causal path. " +
                "If the loop is the thing you wanted to see, the two directions through a topic are usually two " +
                "different events, and drawing them as two destinations models it without the cycle.");
        }

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
        //
        // The budget is the bound that matters and the depth limit is not. This
        // walk enumerates paths rather than visiting each pod once, so a cycle
        // does not lengthen the walk, it multiplies it: at branching factor two,
        // a depth limit of 32 permits on the order of 2^32 paths. On 2026-09-03 a
        // single request through a diagram with three cycles through one pub/sub
        // topic emitted 23,428 spans across a 65-minute trace, and was still
        // emitting after the client that fired it had closed. Depth 32 was never
        // reached and never would have been.
        if (state.Exhausted)
        {
            state.NoteOnce(
                $"Walk stopped at {RunLimits.MaxSpans} spans. The diagram contains a cycle, so " +
                "the request kept arriving back where it started and this run is a truncated " +
                "prefix of an unbounded one rather than a picture of the system. " +
                "Nothing below this point ran, and the hops and timings above it are still real.");
            return;
        }

        if (depth > RunLimits.MaxDepth)
        {
            // NoteOnce, not Note. This fired on every branch that reached the
            // limit, so a looping diagram did not produce "a note about a cycle",
            // it produced thousands of identical copies of one.
            state.NoteOnce($"Walk stopped at depth {RunLimits.MaxDepth}, the diagram contains a cycle.");
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

            activity.SetTag(ShoeboxConstants.TagKey, Baggage.GetBaggage(ShoeboxConstants.TagKey));
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
            // Checked per edge, not only on entry. A pod fanning out to six
            // consumers would otherwise spend six times the budget past the point
            // it ran out, once for every branch already inside the loop.
            if (state.Exhausted) break;

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
                state.NoteOnce($"phantom on a direct call to {target.ServiceName} is ignored: a service that is not there would refuse the connection and the trace would show the error. Put it behind a queue.");

                if (!state.Enter(target.Id))
                {
                    state.NotReentered(pod.Id, target.Id);
                    continue;
                }

                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                Visit(graph, target, activity, state, depth + 1);
                state.Leave(target.Id);
                continue;
            }

            // A queue is a destination, not a service. The publish belongs to
            // whoever published and the receive to whoever consumed; nothing
            // emits on behalf of the queue itself, because in a real system
            // nothing does.
            // A queue is a destination and never a service, so it never gets an
            // identity of its own. The publish belongs to whoever published and
            // the receive to whoever consumed.
            //
            // Skipping this for a terminal queue was worse than the problem it
            // solved: the queue fell through to being walked as an ordinary pod,
            // and every pod gets its own service.name, so RabbitMQ was being
            // published as a service called rabbitmq. A broker turned into a
            // microservice in everybody's topology.
            //
            // A terminal queue therefore does publish with nothing receiving,
            // which is what it is. If that reads as unconsumed, it is unconsumed:
            // the honest fix is to draw who consumes it, not to withhold the
            // publish.
            if (target.Kind == PodKind.Queue)
            {
                // A destination counts as somewhere the request has been. Without
                // this, "publish, consume, publish to the same topic again" is a
                // loop even when no service repeats.
                if (!state.Enter(target.Id))
                {
                    state.NotReentered(pod.Id, target.Id);
                    continue;
                }

                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                PublishAndDeliver(graph, pod, target, graph.From(target.Id).ToList(), activity, state, depth, instance);
                state.Leave(target.Id);
                continue;
            }

            // Same rule as a queue, and for the same reason. A database, a cache
            // and a third party are things you call, not things that report. None
            // of them run your instrumentation, so none of them has a
            // service.name, and inventing one puts a service called sql-server in
            // a topology where no such service exists.
            //
            // The caller's CLIENT span carries db.system.name or server.address
            // and that is the whole record of the dependency, which is exactly
            // what a real trace looks like.
            if (target.Kind is PodKind.Datastore or PodKind.Cache or PodKind.External)
            {
                state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
                EmitDependencyCall(pod, target, activity, state, instance);
                continue;
            }

            // Datastores, caches and third parties fell out above and are never
            // guarded: they are leaves, they cannot start a cycle, and one
            // database called by three services legitimately appears three times.
            if (!state.Enter(target.Id))
            {
                state.NotReentered(pod.Id, target.Id);
                continue;
            }

            state.Hop(pod.Id, target.Id, failed: false, ms: target.DefaultLatencyMs);
            Visit(graph, target, activity, state, depth + 1);
            state.Leave(target.Id);
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
            publish.SetTag(ShoeboxConstants.TagKey, Baggage.GetBaggage(ShoeboxConstants.TagKey));
            publish.SetTag("service.instance.id", $"{producer.ServiceName}-{instance}");
            foreach (var (k, v) in MessagingTags(queue, "publish", "send", messageId)) publish.SetTag(k, v);
        }

        var received = false;
        var declaredPhantom = false;

        foreach (var call in consumers)
        {
            if (state.Exhausted) break;

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
                declaredPhantom = true;
                state.Phantoms.Add(consumer.ServiceName);
                continue;
            }

            if (!state.Enter(consumer.Id))
            {
                // The publish still happened and still names the destination. What
                // did not happen is this consumer running a second time on one
                // causal path, which in a real system is a different message.
                state.NotReentered(queue.Id, consumer.Id);
                continue;
            }

            received = true;
            state.Hop(queue.Id, consumer.Id, failed: false, ms: consumer.DefaultLatencyMs);
            Visit(graph, consumer, publish, state, depth + 1, via: (queue, messageId));
            state.Leave(consumer.Id);
        }

        // A publish with no receive is the signature of a phantom, and it is not
        // only produced by declaring one. Draw a queue as the last node and this
        // loop has nothing to walk, so the run emits exactly what the phantom
        // example emits: same span, same destination, no consumer. A backend
        // cannot tell the two apart, and it does not try — it reports the
        // destination as unconsumed once it stops waiting.
        //
        // That is an asymmetry worth saying out loud rather than fixing in the
        // emitter. A datastore is allowed to be the last thing in a diagram,
        // because a trace only ever learns about one from its caller. A queue is
        // not, because a queue has a far side and the whole reason to draw one is
        // what happens over there. The publish itself is honest and stays: the
        // caller really did publish, and dropping the span would leave an arrow
        // with nothing behind it. What was missing is the sentence explaining
        // what the diagram just claimed.
        if (!received && !declaredPhantom)
        {
            state.Note(consumers.Count == 0
                ? $"Nothing is drawn consuming {queue.Label}. The publish still happens and still names the destination, so anything reading the telemetry reports it as unconsumed. Draw a consumer, or say {queue.Id} -->|phantom| svc if a consumer that never runs is what you mean."
                : $"Every consumer of {queue.Label} failed this run, so the publish has no receive. Until one succeeds, the destination reads as unconsumed to anything inferring topology from the messaging spans.");
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
        yield return new("messaging.system", MessagingSystem(queue));
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
    /// The broker, read off the label instead of asserted.
    ///
    /// This was hardcoded to rabbitmq for every queue, so a queue somebody called
    /// Kafka published as RabbitMQ. It is not only a wrong attribute: a reader
    /// keys a messaging dependency on (system, destination) and labels the node
    /// with the system, so every queue in a diagram rendered under the same name,
    /// and a queue going dark read as "RabbitMQ went phantom" whatever it was
    /// called.
    ///
    /// Registered values only, and rabbitmq when the label does not name one. A
    /// queue nobody named has to be something, and inventing a system name is the
    /// same fabrication as inventing an attribute.
    /// </summary>
    private static string MessagingSystem(Pod queue)
    {
        var label = queue.Label.ToLowerInvariant();

        if (label.Contains("kafka")) return "kafka";
        if (label.Contains("sqs")) return "aws_sqs";
        if (label.Contains("servicebus") || label.Contains("service bus")) return "servicebus";
        if (label.Contains("eventhub") || label.Contains("event hub")) return "eventhubs";
        if (label.Contains("eventgrid") || label.Contains("event grid")) return "eventgrid";
        if (label.Contains("pubsub") || label.Contains("pub/sub")) return "gcp_pubsub";
        if (label.Contains("activemq")) return "activemq";
        if (label.Contains("rocketmq")) return "rocketmq";
        if (label.Contains("pulsar")) return "pulsar";

        return "rabbitmq";
    }

    /// <summary>
    /// The caller's record of calling something that does not report for itself.
    ///
    /// Everything a trace ever knows about a datastore, a cache or a third party
    /// comes from the client spans of the services that called it. They are not
    /// participants, they are attributes on somebody else's span.
    /// </summary>
    private void EmitDependencyCall(Pod from, Pod to, Activity? parent, RunState state, int instance)
    {
        var source = _pool.For(from.ServiceName, instance);
        using var activity = source.StartActivity(
            SpanName(to),
            ActivityKind.Client,
            parent?.Context ?? default,
            startTime: state.Clock);

        state.Clock = state.Clock.AddMilliseconds(to.DefaultLatencyMs);
        if (activity is null) return;

        activity.SetEndTime(state.Clock.UtcDateTime);
        state.SpanCount++;
        activity.SetTag(ShoeboxConstants.TagKey, Baggage.GetBaggage(ShoeboxConstants.TagKey));
        foreach (var (k, v) in SemanticTags(to, ActivityKind.Client)) activity.SetTag(k, v);
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
        activity.SetTag(ShoeboxConstants.TagKey, Baggage.GetBaggage(ShoeboxConstants.TagKey));
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
                // Nothing. Messaging semantics belong on the publish and the
                // receive, which PublishAndDeliver emits, and they only exist when
                // the diagram models the far side of the queue.
                //
                // This was putting messaging.system on the queue's own span for a
                // terminal queue, where by definition we decided not to model the
                // hop. It also omitted the deprecated destination spelling that a
                // reader keys on, so the destination came back empty and the key
                // collapsed to the bare system name: a node called "rabbitmq",
                // which is the broker rather than anything in the diagram.
                //
                // And it asserted rabbitmq for every queue regardless of the label
                // on it. A queue somebody called Kafka reported as RabbitMQ.
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
        /// For notes a bounded walk reaches many times over. The cycle warning is
        /// one sentence about the diagram, not one sentence per truncated branch.
        /// </summary>
        public void NoteOnce(string n)
        {
            if (!_notes.Contains(n, StringComparer.Ordinal)) _notes.Add(n);
        }

        /// <summary>
        /// Whether this run has spent its span budget. See <see cref="RunLimits"/>
        /// for why the budget rather than the depth limit is the real bound.
        /// </summary>
        public bool Exhausted => SpanCount >= RunLimits.MaxSpans;

        /// <summary>
        /// The pods on the current root-to-leaf path. Added on the way down and
        /// removed on the way back up, so it describes where this request has
        /// been, not everywhere the walk has ever been.
        ///
        /// This is what makes a cyclic diagram finite without cutting anything
        /// off. A trace is a tree; a topology is a graph, and a graph with a
        /// cycle in it is an ordinary architecture rather than a mistake. Walking
        /// each causal path without repeating a pod on it is how one turns into
        /// the other. The pub/sub diagram that ran unbounded on 2026-09-03 comes
        /// out as 44 spans, complete, with nothing truncated.
        ///
        /// Per path, emphatically not per run: a shared database called by three
        /// services has to appear three times, and it does, because those are
        /// three different paths.
        /// </summary>
        private readonly HashSet<string> _path = new(StringComparer.Ordinal);

        public bool Enter(string podId) => _path.Add(podId);

        public void Leave(string podId) => _path.Remove(podId);

        private readonly List<NotTaken> _notTaken = new();
        private readonly HashSet<string> _notTakenSeen = new(StringComparer.Ordinal);

        /// <summary>
        /// Pod ids, not service names, so a renderer can key onto the nodes it
        /// already drew. Deduplicated: one edge declined on forty different paths
        /// is one fact about the diagram, not forty.
        /// </summary>
        public void NotReentered(string fromPodId, string toPodId)
        {
            if (!_notTakenSeen.Add($"{fromPodId}->{toPodId}")) return;

            _notTaken.Add(new NotTaken(fromPodId, toPodId,
                "already on this request's path — a request does not visit the same pod twice on one causal path"));
        }

        /// <summary>Every edge refused at least once, before filtering to those never crossed.</summary>
        public IReadOnlyList<NotTaken> DeclinedEdges => _notTaken;

        /// <summary>
        /// These were being collected and thrown away. The cycle warning has been
        /// written by the walk and dropped on the floor since it was added, so a
        /// diagram that loops has been silently truncating at depth 32 with
        /// nothing said about it.
        /// </summary>
        public IReadOnlyList<string> Notes => _notes;
    }
}
