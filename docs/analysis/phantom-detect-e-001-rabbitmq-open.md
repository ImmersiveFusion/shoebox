# Open Investigation — RabbitMQ Still Reads as a Phantom in DeepCube

**PS ID:** phantom-detect | **Entry ID:** e-001 | **Criticality:** C2
**Opened:** 2026-08-23 | **Status:** OPEN — not reproduced under controlled conditions
**Scope boundary:** This document records an unresolved observation, the procedure that
would settle it, and the dead ends already paid for. It asserts no root cause.

---

## 1. The observation

After the emitter fixes of 2026-08-23 shipped, **RabbitMQ still appeared as a phantom
node** in the DeepCube grid while looking at Shoebox output.

**Read the next paragraph before acting on anything below it.** The graph was never
observed directly during that session. Every explanation offered for the behaviour was
inferred from reading source, and seven successive inferences were wrong (§5). Treat the
reasoning here as a list of things to check, not as a finding. A fresh observation
outranks all of it.

---

## 2. Check these two first

Both are load-bearing, both are cheap, and either one explains the observation without
the shipped build being wrong.

### 2.1 A phantom node never un-phantoms itself

`ServiceDiagnostics.TryDemoteDependencyPhantom(key)` has exactly two call sites, and
both are arrival handlers:

- `PhantomDetectorControl.cs:165` — in `RecordConsumer`, keyed on the messaging destination
- `PhantomDetectorControl.cs:214` — in `RecordServer`, keyed on the host

A node therefore demotes **only when a span arrives at that node's key**. Nothing ages a
phantom out, and nothing re-evaluates one on a later scan. Shoebox no longer emits
anything keyed `rabbitmq` at all, so a phantom minted earlier in a session sits in the
grid indefinitely: unrefreshed, unfalsifiable, and indistinguishable from a live finding.

**Procedure.** Clear the grid or restart the client, fire exactly one run, then wait the
full promotion threshold (§3). If RabbitMQ returns, it is real. If it does not, the
observation was a residue of an earlier build.

### 2.2 The diagram lives in the URL fragment

`readDiagramFromUrl()` (`src/Shoebox.Spa/src/app/shoebox/diagram-url.ts:59`) reads
`window.location.hash` and wins over the default example
(`shoebox.component.ts:83`). A browser tab opened earlier in a session is therefore
still running **whatever diagram was in the address bar when it was opened** — including
the older one where `rabbit[[RabbitMQ]]` was a terminal queue with nothing consuming it.

That diagram *should* produce an unconsumed destination. That is the feature working.
Check the address bar before concluding anything; a fresh tab on `/` loads the current
example.

---

## 3. What the consuming side actually does

`IF.APM.App.Unity.HDRP/.../Grid/Scenes/Service/Scripts/Phantom/PhantomDetectorControl.cs`,
read and confirmed 2026-08-23. Two independent rules, keyed differently, with different
outcomes. Confusing them is how a session gets lost.

| | HP-3 (messaging) | HP-4 (hosts) |
|---|---|---|
| Trigger | `Producer` span with a destination (`IndexProducer`, `:139`) | `Client` span with a resolvable peer (`IndexClient`, `:169`) |
| Key | `{messaging.system}-{destination}` (`BuildMessagingKey`, `:331`) | `server.address` → `net.peer.name` → `peer.service` (`BuildHostKey`, `:346`) |
| Resolved by | a `Consumer` span at the same key (`:155`) | a `Server` span at the same key (`:204`) |
| On promotion | marks the **existing** queue node dark in place, preserving edges and layout (`PromoteStaleProducers`, `:231`) | **mints** a new node and wires caller→phantom edges (`PromoteStaleClients`, `:276`) |

**HP-3 does not create a node named after the missing service.** You will never see a
node called Payment Service appear. You see the destination go dark. There is one mint
fallback, for when the producing service was still warming and its queue dependency node
was never created, but that mints under the *destination* key too, never a service name.

**Promotion threshold is 60 seconds** (`PreferencesManagerControl.cs:66`,
`PhantomNodeThresholdSeconds = 60`), scanned once a second
(`ScanIntervalSeconds`, `PhantomDetectorControl.cs:38`). Nothing appears immediately, and
a check made inside the first minute proves nothing.

### 3.1 Two defects in the consumer, neither of them Shoebox's

1. **`BuildMessagingKey` never reads `messaging.destination.name`.** It reads the
   deprecated `messaging.destination`, falling back to a legacy `message.destination`
   (`:334`) — and its own comment calls the deprecated spelling "the OTel-standard" one.
   Shoebox dual-emits both spellings as a workaround (`TopologyRunner.cs:330` and `:340`).

   **This is also why Snowglobe's phantom grid works today**: Snowglobe emits the old
   name. That makes it a sequencing hazard, not merely a defect — **modernising
   Snowglobe's messaging attributes without fixing this line first will break the live
   grid.** See the remediation-status addendum in
   [`../verification/otel-conf-e-001-semconv-vcrm.md`](../verification/otel-conf-e-001-semconv-vcrm.md).

2. **HP-4 never calls `TryMarkDependencyPhantom`.** The producer path marks an existing
   node dark; the client path treats an existing facility as a reason to do nothing —
   at index time (`:181`, `TryGetFacility` then return) and again in the late guard
   (`:296`, `TryGetFacility` then continue). Any peer that already has a facility node,
   which includes every peer the dependency pass has already walked, has therefore always
   been ineligible for promotion. The fix is to mirror the producer path: mark in place
   when the node exists, mint only when it does not.

---

## 4. The standing conformance gap on our side

**`server.port` is deliberately omitted on the phantom path.** The consuming side builds
a dependency key as host → `db.system` → `db.name` → `server.port`, while phantom
detection keys on the host alone. Emitting the port therefore gives one peer two keys, and
the grid shows a duplicate node instead of a promotion.

Omitting it is a real gap against the HTTP client-span conventions, held open on purpose
until the consuming side unifies its two key builders. `server.port` is still emitted
where it is unambiguous (`TopologyRunner.cs:474`, external third-party calls). Do not
"fix" the omission without fixing the key builders first.

---

## 5. Ruled out — do not spend the round again

Each of these was proposed, implemented or argued during the 2026-08-23 session and is
wrong. They are recorded because most of them were plausible.

- **`server.address = "{service}.internal"` on internal spans.** An invented hostname
  that nothing ever emits under, which made *every* service in the diagram a phantom.
- **Emitting `server.port` on the phantom path.** Produces a duplicate node, for the
  reason in §4.
- **Modelling a phantom as a direct call.** A synchronous callee that is not there
  refuses the connection; a refused connection is an error span; an error span is
  evidence. Phantoms are the *absence* of evidence, so they exist only behind a queue.
- **Proposing span Links for the producer/consumer relationship.** Context propagates
  through message headers precisely so the consumer continues the trace. A message that
  starts its own trace is a broken end-to-end view. Parent/child is correct here.
- **Stopping terminal queues from getting producer semantics.** They then fell through to
  being walked as ordinary pods, acquired a `service.name`, and RabbitMQ was published as
  a *service* — worse than the problem it solved.
- **Trusting a running client's grid state across a rebuild.** See §2.1.
- **Trusting a browser tab across a diagram change.** See §2.2.

---

## 6. Method note: print the telemetry

The category error that actually mattered — queues, datastores, caches and third parties
being emitted as services, each with its own `service.name` — was found in one pass by
**building the emitter outside the repository and printing every `service.name` across
every example**. The list had eleven entries and four of them were not services. Seven
rounds of reading code had not found it.

The harness was a console app referencing a copy of `Shoebox.Api`, plus a copy of the test
project with central package management disabled and versions pinned, built in a scratch
directory outside the tree. Building outside the tree is not fussiness: the repo's build
output is routinely locked by a running `Shoebox.Api`, and that lock is what makes in-tree
iteration unreliable for exactly this kind of question. See the README's contributor notes.

The permanent residue of the technique is in the repo:
`tests/Shoebox.Api.UnitTests/Run/QueueShapeDumpTests.cs` asserts on the emitted spans
directly, and `Run/PhantomEmissionTests.cs` covers the phantom path. Extend those before
reaching for a throwaway harness again.

**Do this first, not last. If the claim is about what the telemetry contains, print the
telemetry.**

---

## 7. Also outstanding in Shoebox

- **Destinations are slugified.** Type `orders.created` and the telemetry says
  `orders-created`: queue labels go through the same service-name slugifier as pods
  (`MermaidParser.cs:176`, `Slug` at `:197`). Harmless for the demo, wrong if anyone
  pastes a real destination name in and expects it back out.
- **The example-set analysis was never acted on.**
  [`shoebox-examples-e-001-coverage-gap.md`](shoebox-examples-e-001-coverage-gap.md)
  found four byte-identical cache examples, six of seven "always fails" examples sharing
  one single-edge shape, and six language capabilities with zero coverage. The architect
  pass that was to decide the taxonomy and the cut list has not been run.
- **Nothing is deployed.** No Dockerfile, no workflow, no IaC, and nothing copies the SPA
  build into `wwwroot`. See the README's Status section for the target and the blockers.
