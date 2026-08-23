# Continuation note

Written 2026-08-23, end of a long and frustrating session. For whoever picks this
up, including me.

## The unresolved problem

**RabbitMQ still shows as a phantom in DeepCube.** That is the open item. Everything
else below is context for it.

I never once saw the graph myself. Every claim I made about why it was happening was
inference from reading code, and I was wrong seven times in a row. Do not trust the
reasoning in this file over a fresh observation.

### Prime suspect, and check this first

**Phantom nodes do not un-phantom themselves.** `TryDemoteDependencyPhantom(key)` is
only called from `RecordServer` and `RecordConsumer`, which means a node demotes only
when a span *arrives at that key*. Shoebox no longer emits anything keyed `rabbitmq`,
so a phantom minted earlier in the evening will sit there forever, unrefreshed and
undemoted. It is not evidence that the current build is still wrong.

**Clear the grid or restart the client, then fire once, then wait 60 seconds.**
If RabbitMQ comes back after that, it is real. If it does not, we have been chasing a
ghost of a ghost for the last hour.

### Second suspect

**The diagram lives in the URL fragment.** `readDiagramFromUrl` wins over the default
example, so a browser tab opened earlier still has the old diagram, the one where
`rabbit[[RabbitMQ]]` was a terminal queue with nothing consuming it. That diagram
*should* produce an unconsumed destination, correctly. Check the address bar before
concluding anything. A fresh tab on `/` loads the current example.

## What was actually fixed, and is proven

Each of these has a test or a measurement behind it. 54 tests pass.

- **`messaging.operation.type` was `"publish"`**, which is not a member of the
  enumeration `{create, send, receive, process, settle}`. I invented it. It is `send`
  now. This is the attribute a conformant backend reads to tell a producer from a
  consumer, so nothing could pair the two halves of a queue while it was wrong.
- **Queues, datastores, caches and third parties were being emitted as services**,
  each with its own `service.name`. A two-node SQL diagram was producing a service
  called `sql-server`. They are dependencies: the caller's client span carries
  `db.system.name` or `messaging.destination.name`, and the thing itself emits
  nothing. Fixed for all four kinds.
- **Consumers carried `http.request.method`**, saying a service reached off a queue
  had arrived over HTTP. Messaging spans only now.
- **Shoebox emitted telemetry about itself** under a resource called `shoebox`, so a
  four-service diagram produced five services. Removed entirely.
- **Removing that broke the simulation**, which is the more interesting half. ASP.NET
  leaves an unsampled `Activity` on the request, and a parent-based sampler then drops
  every simulated span beneath it: runs came back with zero spans while hops and notes
  looked fine. The run now detaches from `Activity.Current` and restores it after.
- **`messaging.message.id`** on both halves; **`http.route`** only on SERVER spans.
- **`Pod.DefaultLatencyMs` was dead code.** Spans now get start and end times from a
  modelled clock, so a trace has a readable shape instead of hairlines. A refused call
  is deliberately the *fastest* thing at 2ms, which is the tell worth teaching.
- **`RunState.Note()` collected notes nothing ever read.** The cycle warning has been
  discarded since it was written.

## What I got wrong, so you do not repeat it

- **`server.address = "{service}.internal"`** on every internal span. Invented a
  hostname nothing emits under, so every service in the diagram became a phantom.
- **Emitting `server.port`** on the phantom path. The consuming side builds its
  dependency key as host → `db.system` → `db.name` → `server.port` while phantom
  detection keys on the host alone, so one peer got two keys and you saw a duplicate
  node instead of a promotion. **`server.port` is still deliberately omitted there**,
  which is a real conformance gap awaiting a consuming-side fix.
- **Modelling a phantom as a direct call.** A synchronous callee that is not there
  refuses the connection, which is an error span, which is evidence. Phantoms are the
  absence of evidence, so they only exist behind a queue.
- **Proposing span Links** for producer/consumer. Wrong: context propagates through
  message headers precisely so the consumer continues the trace. A message that starts
  its own trace is a broken end-to-end view. Snowglobe is right to parent them.
- **Stopping terminal queues getting producer semantics.** They then fell through to
  being walked as ordinary pods and got a `service.name`, so RabbitMQ was published as
  a *service*. Worse than the problem it solved.

## The thing that finally worked

**Build the emitter outside the repository and print the spans.**

`scratchpad/probe/` has a console app referencing a copy of `Shoebox.Api`, and a copy
of the test project with central package management disabled and versions pinned. It
runs regardless of what holds the repo's build output, which was locked by a running
instance for most of the evening.

Printing every `service.name` across every example found the category error in one
pass. The list had eleven entries and four were not services. Seven rounds of reading
code had not found it.

**Do this first next time.** If a claim is about what the telemetry contains, print
the telemetry.

## What you need to know about the consumer

`IF.APM.App.Unity.HDRP/.../Phantom/PhantomDetectorControl.cs`, two independent rules:

- **HP-3, messaging.** A `Producer` span's destination key, `{messaging.system}-{destination}`.
  If no `Consumer` arrives for it, the **queue node is marked dark in place**. It never
  mints a named node, so you will never see a node called Payment Service. You see the
  destination go dark.
- **HP-4, hosts.** A `Client` span's peer, resolved from `server.address` →
  `net.peer.name` → `peer.service`. If no `Server` span arrives under that key, the
  peer is promoted.

**Promotion threshold is 60 seconds** (`PhantomNodeThresholdSeconds`), scanned every
second. Nothing appears immediately.

### Two defects in it that are not Shoebox's

1. **`ForMessaging` reads the deprecated `messaging.destination`** and a legacy
   `message.destination`, never `messaging.destination.name`. Shoebox dual-emits both
   spellings as a workaround. **This is also why Snowglobe's phantom grid works** — it
   emits the old name. Modernising Snowglobe's attributes without fixing this line
   first will break the live grid.
2. **HP-4 never calls `TryMarkDependencyPhantom`.** The producer path marks an existing
   node dark; the client path treats an existing facility as a reason to do nothing,
   both at index time and in the late guard. Any peer named by a client span has always
   been ineligible for promotion. The fix is to mirror the producer path.

## Snowglobe, untouched and queued

`docs/verification/otel-conf-e-001-semconv-vcrm.md` is a full conformance matrix with
citations. Verdict on "nothing is invented" was **FAIL**, and Shoebox's half is closed.
Snowglobe's is not:

- **Fabricated attributes in reserved namespaces:** `browser.page`, `db.redis.key`,
  `db.redis.ttl_seconds`, `db.redis.keys_count`, `db.rows_affected`, `db.docs_count`,
  `messaging.batch_size`.
- **Deprecated names throughout:** HTTP since semconv 1.20.0 (April 2023), messaging
  since 1.17.0, database since 1.26.0, and `gen_ai.system` deprecated in 1.37.0, one
  release before the 1.38.0 its own comment claims conformance to.
- `system.memory.usage` is an ObservableGauge where the spec requires UpDownCounter.
- Invalid GenAI enum values: `"retrieve"` should be `"retrieval"`, `"embedding"` should
  be `"embeddings"`.

`IF.APM.OpenTelemetry.Conventions` (in `IF.APM.Ingestion/src/SDK/DotNet/`) is already
current and marks the deprecated names `[Obsolete]`. One defect: its doc comment on
`MessagingOperationType` says *"e.g., publish, receive, process"*, and `publish` is not
in the enum. That comment is IntelliSense-visible and is plausibly where I got it.

## Also outstanding in Shoebox

- **Destinations are slugified.** You type `orders.created`, telemetry says
  `orders-created`. Queue labels go through the service-name slugifier.
- **The example set was analysed and never acted on.**
  `docs/analysis/shoebox-examples-e-001-coverage-gap.md` found four *byte-identical*
  cache examples, six of seven "always fails" being the same single-edge shape, and six
  language capabilities with zero coverage. The architect pass that was meant to decide
  the taxonomy and cut list was never run.
- **Nothing is deployed.** No Dockerfile, no workflow, no IaC, and nothing copies the
  SPA build into `wwwroot`. Target is `shoebox.deepcube.ai`, bound to the same App
  Service `chaos.deepcube.ai` already uses.

## Environment traps

- **The repo build output gets locked** by any running `Shoebox.Api`. `dotnet msbuild
  -t:Compile` type-checks without touching `bin/`. For anything more, copy to scratch.
- **Node on PATH is v24.11.1**; Angular 22 refuses below 24.15.0 and says so in a
  message that looks nothing like a build error. A portable 24.19.0 lives in
  `scratchpad/nodedl/`. `npm ci` only warns, which makes this easy to misdiagnose.
- **Local OTLP settings are in user secrets** (`UserSecretsId` = `shoebox-api`), because
  every settings file here is committed.
- This machine ran out of RAM and disk earlier in the project's history. If a build dies
  for no reason, check free space first.

## Repository state

`feat/synthetic-mermaid`, 39 commits ahead of origin, **nothing pushed**. Working tree
clean. Snowglobe is 1 ahead on `main`, also unpushed. IF.Knowledge.Marketing is 2 ahead
on `trout/snowglobe-rename-and-distribution-playbook`.
