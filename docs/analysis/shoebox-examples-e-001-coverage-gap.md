# Coverage and Taxonomy Gap Analysis — Shoebox Example Set

**PS ID:** shoebox-examples | **Entry ID:** e-001 | **Criticality:** C2
**Analysis type:** gap + risk (taxonomy) | **Method:** S-013 (Inversion, primary), S-004 (Pre-Mortem), S-012 (FMEA), S-003 (Steelman), S-010 (Self-Refine)
**Scope boundary:** This document produces evidence, gaps, and ranked failure modes only. It does **not** select a final taxonomy — that decision belongs to the architect.

---

## Status Addendum (2026-08-23)

*Added after the analysis was written. The body below is unchanged.*

**The architect pass this document was written to feed has not been run.** No taxonomy has
been chosen, no example has been cut, and the four byte-identical cache examples (§4.1) are
all still shipping. Every recommendation in §12 is still open, with one exception:

**§3.1 item 18, E-005 and E-006 are now stale.** `Pod.DefaultLatencyMs` is no longer dead
code. Spans are given start and end times from a modelled clock
(`TopologyRunner.cs:142`, `:286`, `:373`), so a trace now has a readable shape instead of
hairlines, and a refused call is deliberately the *fastest* thing in the trace at 2ms —
which is itself the tell worth teaching. **The prerequisite engineering fix named in §12's
second bullet is therefore done**, and "latency as a lesson" is now a pure curriculum
decision with no code blocking it.

Everything else — the redundancy findings, the zero-coverage capabilities, and the FMEA
scoring of the three candidate taxonomies — stands as written.

---

## 1. Executive Summary (L0)

Shoebox ships 16 example diagrams to teach people how to read distributed traces. We checked what the tool can actually do against what the 16 examples actually show, and found a big gap: the tool supports several genuinely useful lessons — a whole service being down (not just one broken call), a call to an outside company like a payment processor, one step being much slower than the rest — and **none of the 16 examples demonstrate any of them**. Meanwhile, four of the examples (the four "Redis" ones) are exact, byte-for-byte duplicates of each other: pasting any of them and firing the request produces literally the same output, so a curious learner who tries all four learns nothing the first one didn't already teach.

We also stress-tested the three candidate ways to organize the examples (the current "Databases / Workflows / Distributed systems" grouping, the original "SQL / Redis / Saga / Worker" grouping, and a "never fails / always fails / fails sometimes" grouping) against three realistic future scenarios: a phantom service that shows up in a trace but isn't in the diagram, one hop that's just slower than the rest, and a timed-out call to a payment provider. All three groupings struggle with at least one of these, and one of them (grouping by failure frequency) fails badly on all three, because it throws away exactly the information a learner needs — what shape the system is — in favor of a question (how often does it break) that turns out to be the wrong axis entirely.

Recommended next step: before deciding on a taxonomy, decide whether to spend engineering effort activating the latency-simulation code that already exists but is currently dead, and whether to add a "whole service is down" and a "third-party call" example, since those are the highest-value, lowest-cost fixes available and they don't require inventing new mechanics — the mechanics already exist in the code, they are just never demonstrated.

---

## 2. Analysis Scope & Method

Evidence was gathered by direct inspection of:
- `src/Shoebox.Spa/src/app/shoebox/examples.ts` (the 16 examples — data under analysis)
- `src/Shoebox.Api/Topology/MermaidParser.cs` (diagram language surface)
- `src/Shoebox.Api/Topology/Topology.cs` (PodKind, replicas, pinning, latency model)
- `src/Shoebox.Api/Run/TopologyRunner.cs` (what a run actually emits)
- `src/Shoebox.Api/Emit/PodTracerPool.cs`, `src/Shoebox.Api/Emit/OtlpTarget.cs` (checked to verify whether latency modeling is actually consumed at runtime — it is not; see E-010)
- `README.md` (product framing, diagram-language table)

No test execution or live run was performed; conclusions are static-analysis-based and cite line numbers. Where a claim required inference beyond what the code states, it is labeled **[inference]**.

Method: S-013 (Inversion) was applied first to each taxonomy candidate (assume it is the worst reasonable choice, argue why) but only after S-003 (Steelman) built the strongest case for it, per H-16. S-012 (FMEA) was then applied to rank the three taxonomies' failure modes by Severity × Occurrence × Detection. S-004 (Pre-Mortem) was applied to the example set as a whole. S-010 (Self-Refine) pass is recorded in §10.

---

## 3. Expressiveness vs. Use (Task Item 1)

### 3.1 Full capability inventory

| # | Capability | Where defined | Example(s) that exercise it | Coverage |
|---|---|---|---|---|
| 1 | `Service` node (`api[Label]`) | MermaidParser.cs:182 (default shape branch); Topology.cs:10 | All 16 | Full |
| 2 | `Datastore` node (`db[(Label)]`) | MermaidParser.cs:179; Topology.cs:11 | 5 SQL examples | Full |
| 3 | `Queue` node (`q[[Label]]`) | MermaidParser.cs:178; Topology.cs:12 | 3 worker examples (`Job Queue`, `RabbitMQ`) | Partial — only ever inside the one Worker template |
| 4 | `Cache` node (`cache((Label))`) | MermaidParser.cs:180; Topology.cs:13 | 6 Redis examples | Full, but see §4 (4 of 6 are literal duplicates) |
| 5 | `External` node (`ext{{Label}}`, third party) | MermaidParser.cs:181; Topology.cs:14; README.md:43 | **None** | **Zero coverage** |
| 6 | Replicas / load-balanced pool (`Worker x5`) | MermaidParser.cs:33,153-158 | `multi-replica-saga` (x2), `worker-happy`/`worker-broken`/`worker-broken-one` (x5) | Full |
| 7 | Pinned single instance as a node (`Worker #2`) | MermaidParser.cs:36,159-167; Topology.cs:28 (`PinnedInstance`) | **None** | **Zero coverage** — no example names a single instance as a node; only per-call instance targeting (#8) is used |
| 8 | Always-broken call (`-->\|broken\|`, `-->\|broken: reason\|`) | MermaidParser.cs:114-141 | 4 SQL, 2 Redis, `worker-broken` | Full |
| 9 | Per-instance broken call, single instance (`-->\|broken on #3\|`) | MermaidParser.cs:127-134 | `worker-broken-one` | Covered once |
| 10 | Per-instance broken call, **multiple** instances (`-->\|broken on #3,#5\|`) | MermaidParser.cs:130-133 (comma-split loop) | **None** | **Zero coverage** |
| 11 | Failure reason string → span status/`error.type` | MermaidParser.cs:120-124; TopologyRunner.cs:113-116 | 4 SQL, 2 Redis, `worker-broken`, `worker-broken-one` | Full |
| 12 | Whole-pod-down declaration (`class db broken`) — fails every inbound call from every caller | MermaidParser.cs:29-30,55-64,89-98 | **None** | **Zero coverage.** The parser's own doc comment (lines 86-88) states this is "a real skill worth teaching" and is architecturally distinct from a single broken edge — yet it is entirely undemonstrated. |
| 13 | Fan-out (two distinct arrows out of one node, both fire) | README.md:50-51; TopologyRunner.cs:87-99 (loop over `graph.From`) | 3 worker examples (worker → api AND worker → rabbit) | Partial — only ever the same 2-way shape, only inside one template |
| 14 | Chain / single-successor topology | SAGA, SQL, REDIS templates | 12 of 16 examples | Full (over-represented relative to fan-out) |
| 15 | Cycle handling (bounded walk, depth 32) | TopologyRunner.cs:60-66 | **None** | **Zero coverage** — the runner has explicit code to survive a cyclic diagram and note it, but no example ever draws a cycle |
| 16 | Entry-point detection: unique root, first-in-document-order tiebreak | Topology.cs:83-90 | Implicitly all 16 (trivial, single-root diagrams) | The **tiebreak logic itself** (ambiguous entry) is **zero-coverage** — no example has more than one root candidate |
| 17 | No-entry-point failure path (fully cyclic diagram, `RunResult` with a note, no trace) | TopologyRunner.cs:31-36 | **None** | **Zero coverage** |
| 18 | Per-kind default latency (`DefaultLatencyMs`: Cache 1ms, Queue 1ms, Datastore 8ms, External 200ms, Service 15ms) | Topology.cs:31-38 | **None can, because the field is never read anywhere else in the codebase** | **Dead code.** Confirmed by repo-wide grep: `DefaultLatencyMs` has exactly one reference (its own declaration). `TopologyRunner.cs`, `PodTracerPool.cs`, and `OtlpTarget.cs` contain no delay/sleep injection of any kind — span duration is real wall-clock time of the synchronous `Visit()` call, effectively near-zero and uniform regardless of `PodKind`. **This capability does not exist at runtime today**, independent of example coverage. |
| 19 | Round-robin deterministic instance selection (never random) | TopologyRunner.cs:130-135 | `multi-replica-saga`, all 3 worker examples | Full |
| 20 | Baggage-carried `sandbox.id` propagation | TopologyRunner.cs:43,82,117 | All 16 (infrastructural, not example-selectable) | Full but orthogonal to example content |
| 21 | Unknown-line tolerance (parse notes, never throws) | MermaidParser.cs:82-83 | **None** deliberately (no example ships a garbage line to demonstrate the UX) | Zero coverage of the affordance itself, though it's a safety net rather than a taught skill |

### 3.2 Headline finding

Of 21 identified capabilities, **6 have zero example coverage** (External/third-party nodes, pinned single-instance nodes, multi-instance `broken on #N,#M`, whole-pod-down `class ... broken`, cycles, ambiguous/absent entry point), and **1 capability that the type system explicitly models (per-kind latency) is unreachable at runtime regardless of what an example draws** — it would need a code change, not a new example, directly contradicting the project's own stated design law: *"Adding an example must never require code. `examples.ts` is pure data. If a new scenario needs a code change, the design has gone wrong."* (README.md:218-219; examples.ts:4-5). Latency-as-a-lesson is currently impossible to add within that law.

By contrast, chains and always-broken single edges are heavily over-represented: 12 of 16 examples are simple chains, and the "single edge, always broken, distinguished only by a reason string" shape appears **6 times** (see §4).

---

## 4. Redundancy (Task Item 2)

### 4.1 The Redis quartet — verified identical

`REDIS` is a template function (examples.ts:24-25):
```
const REDIS = (extra: string) => `flowchart LR
  api[Orders API] --> cache((Redis))${extra}`;
```
It is invoked with an **empty string** for all four of: `redis-success` (line 70), `redis-missing-key` (line 72), `redis-large-value` (line 74), `redis-expired-key` (line 76). `REDIS('')` produces the exact same two-line string every time — not merely the same topology, the same *bytes*, no label, no broken clause. Because `MermaidParser.Parse` is a pure function of the diagram string (MermaidParser.cs:39-106) and `TopologyRunner.Run` is a pure function of the parsed `Graph` plus `runIndex` (Run.cs:29-56), and none of these four examples touches replicas, pinned instances, or `runIndex`-sensitive selection (`cache` has `Replicas=1`, so `SelectInstance` always returns `1`, TopologyRunner.cs:133), **the four examples are provably identical in topology, in parsed graph, and in emitted spans** — not just similar. Their `description` fields promise different semantics ("returns null," "10KB payload," "expires immediately") that **the engine has no mechanism to express**: there is no return-value model, no payload-size model, and no TTL model anywhere in `Topology.cs` or `TopologyRunner.cs`. This is the strict form of the redundancy check the task requested (identical topology **and** identical run behavior), and it holds for exactly this one cluster of four.

### 4.2 Applying the same strict test elsewhere

Checked every other pair/group in the 16 for byte-identical diagram text: **none found.** Every other example differs from every other by at least one label. So the strict "identical string, identical behavior" test yields exactly one redundant cluster, of size 4.

### 4.3 A softer, pedagogically-relevant redundancy the strict test misses

Widening the test from "identical" to "same shape, distinguished only by a cosmetic label" surfaces a second, larger cluster: `sql-wrong-table`, `sql-wrong-column`, `sql-syntax-error`, `sql-division-error`, `redis-serialization-error`, `redis-invalid-operation` — **6 examples**, all of the form *one edge, always broken, distinguished only by the string after the colon* (examples.ts:57-82). Structurally, every one of these produces: one healthy-looking parent span, one child span with `ActivityStatusCode.Error` and a distinct `error.type`/status description (TopologyRunner.cs:113-116), no fan-out, no replica, no chain depth beyond 2. From the standpoint of "reading a distributed trace" (as opposed to "knowing SQL error taxonomy"), these 6 examples teach the *same single reading skill six times* — "find the span with error status and read its message" — with the only variance being the message text. This is a design choice, not a bug (the source header at examples.ts:6-11 says as much for the SQL four: "the four SQL failures draw an identical picture and differ only in how the call fails"), but it means the code's own documentation **understates** the redundancy: it calls out only the SQL four sharing a *topology*, not that the Redis four (§4.1) share the topology, the label, and the behavior — a strictly stronger degeneracy that the comment doesn't mention at all.

### 4.4 What is *not* redundant, despite surface similarity

`worker-happy`, `worker-broken`, `worker-broken-one` (examples.ts:93-101) share one template (`WORKER`, lines 33-38) and therefore one topology, but each produces materially different telemetry: 0% failure, 100% failure (every one of 5 instances fails, MermaidParser.cs "broken" with no `on` clause → `BrokenInstances` empty means "all," Topology.cs:60), and 20% failure recurring on a fixed instance (round-robin over 5, MermaidParser.cs:127-134). `worker-broken-one` is the **only** example in the set that demonstrates intermittent/partial failure across repeated fires — it is correctly *not* redundant, and its uniqueness is itself a finding (see §5, gap c).

---

## 5. What a Learner Needs (Task Item 3)

Gap analysis against the stated skill ("read a distributed trace"), decomposed into the recognitions a learner must build:

| Skill to recognize | Demonstrated by | Coverage |
|---|---|---|
| (a) Healthy span tree, parent→child, all OK | `sql-success`, 4× `redis-*` (dupes), `simple-saga`, `multi-replica-saga`, `worker-happy` | Strong (arguably over-served — 8 of 16 examples are pure-healthy variants of only 2 real shapes) |
| (b) Error status + message on a failed span | 4 SQL + 2 Redis + `worker-broken` (7 examples) | Strong, but see §4.3 — repetitive past the first 1-2 |
| (c) Partial/intermittent failure across repeated fires | `worker-broken-one` only | **Thin — single point of failure for an entire skill.** It is also the `DEFAULT_EXAMPLE` (examples.ts:104), so a visitor who never opens the picker sees this lesson exactly once and has no second instance to compare it against. |
| (d) Fan-out (one parent, ≥2 concurrent children) vs. a chain | Fan-out: 3 worker examples, always the same 2-way shape (worker→api, worker→rabbit). Chain: 12 of 16 examples. | **Fan-out is present but narrow** — no 3-way fan-out, no fan-out at the entry point (e.g., a gateway calling two services directly), which is arguably the most commonly-encountered fan-out shape in real systems |
| (e) One slow hop dominating total latency | **None**, and per §3.1 item 18 the mechanism to demonstrate it does not currently execute at runtime | **Zero, and currently un-buildable as a pure-data example** |
| (f) A span that has no business existing (phantom/extra span not implied by the diagram) | **None**; no mechanism exists in `TopologyRunner` to emit a span outside the parsed graph walk | **Zero, and currently un-buildable as a pure-data example** — same class of gap as (e) |
| (g) Replica/instance identification — "which copy served this run" | `multi-replica-saga`, all 3 worker examples (`service.instance.id` tag, TopologyRunner.cs:83; `ServedBy` list) | Strong |
| (h) Downstream service fully down vs. one call broken | Mechanism exists and is explicitly called "a real skill worth teaching" in the parser's own comment (MermaidParser.cs:86-88) | **Zero** — direct contradiction between the code's stated intent and the example set |
| (i) Third-party/external dependency semantics (different attributes: `server.address`, `http.request.method=POST`, distinct baseline latency) | `ext{{...}}` shape exists (README.md:43; Topology.cs:14; TopologyRunner.cs:168-170) | **Zero** |

**Net: of 9 identified reading skills, 2 are well-served but over-repeated (a, b), 1 is served by a single fragile instance (c), 1 is narrowly served (d), and 4 are entirely unaddressed (e, f, h, i) — two of which (e, f) cannot be fixed by adding an example under the project's current "no code change" design law, and two of which (h, i) could be fixed with zero code changes since the mechanisms already exist and are simply unused.**

### 5.1 An adjacent finding (flagged, not scored into the taxonomy critique)

`TopologyRunner.cs:157` hardcodes `db.system.name = "postgresql"` for every `Datastore`-kind pod regardless of its drawn label. All 5 SQL examples label the node `SQL Server` (examples.ts, `SQL` template line 22) but the emitted span's semantic-convention attribute says `postgresql`. This directly contradicts the README's claim that "the shapes already map onto OpenTelemetry semantic conventions" (README.md:34-35) and is the kind of label/telemetry mismatch a careful learner would (rightly) flag as a bug rather than a lesson. **[inference: likely unintentional]** — noted here because it bears on learner trust in the "diagram is the whole state" pitch (README.md:12-13), but it is an implementation defect, not a taxonomy question, and is out of scope for the architect's grouping decision.

---

## 6. Taxonomy Critique via Inversion (Task Item 4)

Three candidates are on the table. Each is steelmanned (S-003) before being inverted (S-013).

### 6.1 Current: Databases (11) / Workflows (3) / Distributed systems (2)

**Steelman:** This groups by the mental posture a reader needs, not by implementation vocabulary — "am I reading a data-access span, an orchestration chain, or a multi-instance/scaling scenario" is a real, distinct cognitive mode for each bucket, and it already fixed a documented problem (examples.ts:41-46: the old grouping mixed "two vendors, a flow pattern and a role," four incompatible axes). The 11/3/2 skew could also be defended as mirroring reality: database-access confusion is plausibly the single most common real-world telemetry-reading problem, so weighting the curriculum there is not obviously wrong. **[inference]**

**Inversion — assume this is the worst reasonable option:**
- "Databases" is 69% of the set (11/16) and is structurally a dumping ground: *anything drawn with a cylinder or a circle* lands here by construction (MermaidParser.cs:179-180), with no further discrimination between "query correctness" failures (SQL) and "cache semantics" failures (Redis) that are pedagogically distinct. Every future cache/queue-adjacent/NoSQL example has an automatic, low-friction home here, which will only worsen the skew — there is nothing in the taxonomy itself that resists growth in this bucket.
- "Distributed systems" (2 examples) is close to vacuous as a label: **every** example in the set technically involves multiple pods calling each other over simulated hops, i.e., is "a distributed system." The name doesn't say what actually distinguishes these two (replica/instance-selection mechanics) — a new-example author has no way to derive from the group name what belongs there.
- It offers no answer to where "third-party call" or "latency" examples go (tested formally in §7).

### 6.2 Rejected alternative 1: SQL / Redis / Saga / Worker

**Steelman:** Maximally concrete, zero abstraction cost for a beginner — "show me what a broken Redis call looks like" maps directly onto a group literally named Redis. It costs nothing to maintain because it already matches the implementation (`SQL`, `REDIS`, `SAGA`, `WORKER` are literally the four template functions in the source, examples.ts:21-38), so it's "free" and matches how a working engineer actually searches (by product name, not by abstract kind).

**Inversion:** The code's own comment already diagnoses this axis inconsistency (examples.ts:43-44): SQL and Redis are vendor/product nouns, Saga is an architectural pattern, Worker is a component role — four incompatible category types in one list. The failure mode this produces is structural, not cosmetic: **any future example that isn't literally one of these four nouns has no candidate bucket at all** — not an ambiguous one, an absent one. A phantom-service example, a latency example, or a third-party-timeout example would each require inventing a brand-new, differently-shaped bucket (a fifth axis on top of four already-incompatible ones), which is the taxonomy failing to answer the extensibility question even in principle, not just in practice. Numbers if reconstructed: SQL=5, Redis=6, Saga=2, Worker=3 — a comparable skew to the current taxonomy (Redis is the plurality at 6/16), so it doesn't even solve the imbalance problem it would need to solve to be worth the axis-inconsistency cost.

### 6.3 Rejected alternative 2: failure-frequency axis (never fails / always fails / fails sometimes)

**Steelman:** This targets the tool's actual core claim head-on — "learn to read telemetry" is fundamentally about distinguishing healthy, broken, and flaky spans, which is a behavior/outcome question, not a topology question. A learner arriving with "what does an intermittent failure look like" is served directly by a group literally named for that, in a way neither other taxonomy names explicitly. It also makes the single most pedagogically important existing example (`worker-broken-one`, §5c) visible and findable by name instead of buried under a vague "Distributed systems" label.

**Inversion:** Reconstructing the counts: **never fails = 8** (`sql-success`, 4× `redis-*`, `simple-saga`, `multi-replica-saga`, `worker-happy`), **always fails = 7** (4 SQL-broken, 2 Redis-broken, `worker-broken`), **fails sometimes = 1** (`worker-broken-one`). This is the worst imbalance of the three candidates (8/7/1 vs. 11/3/2 vs. 5/6/2/3) and produces a group of exactly one member, which is not really a "group" — it's a label for a single outlier, guaranteed to look thin the moment a second visitor asks "is that it?" (S-004 pre-mortem angle). Far more seriously, this axis is **orthogonal to system shape** and actively discards the information the current taxonomy (however imperfectly) preserves: a healthy SQL call and a healthy 5-hop saga chain — two completely different things to *look at* — land in the same bucket purely because both currently succeed, while a broken SQL call and a broken Redis call — structurally identical single-edge failures (§4.3) — get no discrimination either, since both are simply "always fails." The taxonomy answers a real question (how often does this break) but it is the wrong question for organizing a *reading* curriculum, because reading is about shape, and this axis is blind to shape by construction.

### 6.4 Summary of imbalance numbers

| Taxonomy | Bucket sizes | Max/min ratio | Zero/near-zero buckets |
|---|---|---|---|
| Current (3 groups) | 11 / 3 / 2 | 5.5:1 | none, but 2 is thin |
| SQL/Redis/Saga/Worker (4 groups) | 5 / 6 / 2 / 3 | 3:1 | none |
| Failure-frequency (3 groups) | 8 / 7 / 1 | 8:1 | 1 (functionally a singleton) |

---

## 7. Extensibility Test (Task Item 5)

For each of three concrete new scenarios, does each taxonomy answer "where does it go?" unambiguously?

| New scenario | Current (Databases/Workflows/Distributed systems) | SQL/Redis/Saga/Worker | Failure-frequency |
|---|---|---|---|
| (a) Phantom service — appears in trace, absent from the pasted diagram | **Fails.** No group name describes "the diagram lied." Could be argued into "Distributed systems" only by stretching the label to mean "something weird about multi-pod behavior," which is guesswork, not an answer. **Also note:** this requires a new runtime capability — nothing in `TopologyRunner.Visit` can currently emit a span outside the walked graph — so it is orthogonal to which bucket it lands in. | **Fails, worse.** None of the four nouns (all concrete components) connote "an observability/topology-truthfulness problem" even loosely; would require an outright 5th bucket. | **Fails categorically.** The scenario isn't about failure frequency at all — a phantom span isn't a failed call, it's an extra one — so the axis can't even ask the right question. Forcing it into "never fails" (because nothing errors) actively hides the point of the example. |
| (b) One hop 200ms slower than the rest | **Ambiguous/fails.** No group is about performance; would default into "Distributed systems" as a residual catch-all, diluting a bucket that's already vague (§6.1). | **Answerable but fragments the concept.** A slow SQL call fits "SQL," a slow Worker call fits "Worker" — placement is technically possible, but "read a latency outlier" gets scattered across every bucket instead of taught as its own recognizable skill. | **Fails categorically.** Latency is a duration, not a boolean failure state; the axis has no vocabulary for "slow but successful," which is precisely the case this scenario tests. |
| (c) Third-party payment provider call that times out | **Ambiguous.** Could plausibly be filed under "Workflows" (a step in a chain) or "Distributed systems" (multi-party failure) with no principled tiebreaker between the two. | **Fails.** No noun among the four describes an external/third-party dependency at all; requires a new (5th) bucket. | **Answers unambiguously** ("always fails," if it always times out) — but only because the axis throws away everything that makes this scenario notable (it's a `PodKind.External` call with distinct attributes and baseline latency, currently zero-coverage per §3.1 item 5); the bucket would be indistinguishable from a broken SQL call. Unambiguous placement, semantically empty result. |

**Net:** no taxonomy among the three passes all three tests cleanly. The current taxonomy is ambiguous on all three (never a hard *fail*, never a clean *pass*). SQL/Redis/Saga/Worker hard-fails on (a) and (c) because its vocabulary is closed over four existing nouns. The frequency axis is the only one that ever gives an unambiguous placement (c), but it does so by discarding the substance of two of the three scenarios (a, b) and reducing the third to a duplicate of an unrelated example — i.e., it is "unambiguous" only because it stops asking the useful question.

---

## 8. FMEA — Ranked Failure Modes of the Taxonomy Options (S-012)

| Taxonomy | Failure Mode | Effect | Cause | S | O | D | RPN |
|---|---|---|---|---|---|---|---|
| Frequency axis | Orthogonal-axis conflation: unrelated topologies share a bucket because both currently succeed/fail | Curriculum organized around outcome hides the shape lessons; miseducates by implying "healthy SQL call" and "healthy 5-hop saga" are the same kind of thing | Axis measures behavior, not structure | 8 | 9 | 8 | **576** |
| Current | "Distributed systems" label is near-vacuous (every example qualifies technically) | New-example authors can't derive placement from the name; bucket stagnates or gets misused as a catch-all | Label names a mechanic (replicas/instances) but reads as a universal descriptor | 5 | 7 | 6 | **210** |
| Current | "Databases" dumping-ground growth (69% already, no resistance to further growth) | Curriculum skew worsens with every future cache/queue/NoSQL example; other lessons stay starved | Any cylinder/circle-shaped node defaults here by construction | 6 | 8 | 3 | **144** |
| SQL/Redis/Saga/Worker | Closed-vocabulary axis: any capability outside the 4 existing nouns has no candidate bucket at all (not ambiguous — absent) | Every future new-shape example (External, phantom, latency) forces an ad hoc new bucket; taxonomy never stabilizes | Buckets are named after existing implementation templates, not extensible concepts | 7 | 9 | 2 | **126** |

**Reading:** The frequency axis is the highest-risk option by a wide margin (RPN 576) precisely because its failure mode is silent — it doesn't produce an obvious error like "this doesn't fit anywhere," it produces confident-looking but wrong placements (Severity 8, Detection 8 — hard to notice without deliberately testing extensibility, as done in §7). The current taxonomy's two failure modes are lower-severity but higher-occurrence/lower-detection-difficulty (they're already partially visible in the 11/3/2 split, D=3 for the Databases mode). SQL/Redis/Saga/Worker's failure mode has the lowest Detection score (2) because the codebase's own comment already names the problem (examples.ts:43-44) — it's the most self-evidently broken of the three, which is presumably why it was already rejected once.

---

## 9. Pre-Mortem (S-004): How This Looks Embarrassing in Six Months

Because the diagram source is plain text carried in a shareable URL fragment (README.md:27-30), it is trivially inspectable and shareable by any curious visitor. The most likely embarrassing outcomes, in descending order of how easy they'd be for an outside observer to produce:

1. Someone fires `redis-success`, `redis-missing-key`, `redis-large-value`, and `redis-expired-key`, diffs the four resulting traces, finds them byte-identical, and posts about it — directly undermining the tool's headline promise ("the diagram is the whole state," "fire one request... you know what you broke," README.md:12-13, 20-22) with its own output as evidence.
2. A user asks, in a forum or issue, "how do I show a dependency that's completely down, not just one failing call?" and is pointed at `class db broken` in the parser comment — a capability that has existed since the parser was written but that the maintainers' own example set never demonstrated, making the omission look like an oversight rather than a decision.
3. Someone asks for a "slow but not broken" example and discovers, on inspection of the code, that `DefaultLatencyMs` (Topology.cs:31) is entirely unused — a modeled-but-inert field — which reads as an unfinished feature left visible in the type system.
4. A blog post or conference talk uses Shoebox to illustrate "reading telemetry" and reaches for exactly the three scenarios tested in §7 (phantom service, slow hop, third-party timeout) because they are the three most common real debugging stories — and finds none of the 16 examples cover any of them.

---

## 10. Self-Refine Pass (S-010, H-15)

- Every causal/redundancy claim above is tied to a specific line citation; the one place inference was used without a direct citation is flagged **[inference]** (§6.1 database-skew defense, §5.1 bug-vs-feature judgment).
- Checked for over-reach: the analysis does **not** claim the current taxonomy is worse than the alternatives overall — §6.1's inversion is deliberately adversarial per the requested method, and §6.1's steelman is preserved alongside it so the architect sees both sides.
- Checked that "zero coverage" claims are falsifiable: each one names the specific file/line where the capability is defined and confirms (via grep for `DefaultLatencyMs`, and via manual scan of all 16 diagram strings for `{{`, `class`, `#`-pinning, and comma-separated `on` clauses) that no example invokes it.
- Assumption made explicit: this analysis treats "distinguishable run behavior" as topology + parsed `Call` fields (`Broken`, `BrokenInstances`, `FailureReason`) + replica count/pinning, since those are the only run-time-visible differentiators the code exposes. It does not attempt to run the API and diff actual OTLP payloads; the equivalence claims in §4 are derived from tracing the code path, not from an executed test. Confidence in §4.1 (Redis quartet) is **high** (pure-function argument, no external state). Confidence in the taxonomy inversions (§6–§8) is **medium** — they are structured argument and enumerated evidence, not a controlled experiment, and reasonable people could weight severity/occurrence/detection differently in the FMEA.

---

## 11. Conclusions

1. The example set is more redundant than its own documentation admits: the code comment (examples.ts:6-11) flags the SQL-four sharing a topology, but doesn't mention that the Redis-four are stronger duplicates — identical strings, not just identical shapes (§4.1).
2. Roughly a third of the enumerated capabilities (6 of 21, §3.1) have zero example coverage, and two of those capabilities were explicitly called "a real skill worth teaching" in the parser's own source comments (whole-pod-down, §5h) — the gap is not for lack of a mechanism.
3. One capability the type system models (per-kind latency) cannot be demonstrated by any example today because it is dead code at runtime (§3.1 item 18) — this is a prerequisite engineering fix, not a curriculum fix, and it blocks an entire learner skill (§5e) that the "no code change per example" design law otherwise could never unblock.
4. All three candidate taxonomies fail at least one of the three extensibility probes in §7; none is a clean winner. The frequency axis is categorically the riskiest by FMEA (RPN 576) because its failures are silent and structural, not because it never appears to place things ambiguously — it appears certain and is wrong.

## 12. Recommendations (evidence for the architect's decision, not a decision)

- Before choosing a taxonomy, resolve whether "External/third-party" and "whole-pod-down" get example coverage — both are zero-cost from an engineering standpoint (mechanisms already exist) and both are unambiguous evidence gaps independent of which taxonomy wins.
- Treat "latency as a lesson" as a separate, prior decision: it requires activating dead code (Topology.cs:31-38 wiring into TopologyRunner) before any example can teach it, and that decision should not be made implicitly by a taxonomy choice.
- Whatever taxonomy is chosen, run the three §7 probes (phantom service, slow hop, third-party timeout) against it explicitly and require a documented, non-guessed answer for each before shipping the taxonomy change — this analysis shows all three current candidates fail at least one probe silently if not checked.
- Consider collapsing or relabeling the Redis quartet (§4.1) regardless of taxonomy outcome — it is a correctness/credibility issue in the example set itself, orthogonal to how examples are grouped.

## 13. Evidence Summary

| Evidence ID | Type | Source | Relevance |
|---|---|---|---|
| E-001 | Code | examples.ts:24-25, 69-76 | Proves `REDIS('')` produces byte-identical strings for 4 examples (§4.1) |
| E-002 | Code | examples.ts:6-11, 43-46 | Project's own prior self-critique of redundancy and axis-inconsistency |
| E-003 | Code | MermaidParser.cs:29-30, 55-64, 86-98 | Whole-pod-down mechanism + its own "real skill worth teaching" claim, zero example coverage |
| E-004 | Code | MermaidParser.cs:181; Topology.cs:14; README.md:43 | `External`/third-party node type exists, zero example coverage |
| E-005 | Code | Topology.cs:31-38 | `DefaultLatencyMs` defined |
| E-006 | Grep | repo-wide search for `DefaultLatencyMs`/`Delay`/`Sleep` | Confirms zero runtime consumption — dead code (§3.1 item 18) |
| E-007 | Code | TopologyRunner.cs:60-66, 31-36 | Cycle-bound and no-entry-point branches exist, zero example coverage |
| E-008 | Code | TopologyRunner.cs:130-135 | Deterministic round-robin instance selection, basis for §4.4 non-redundancy argument |
| E-009 | Code | TopologyRunner.cs:157 vs. examples.ts:22 | `db.system.name="postgresql"` hardcoded regardless of "SQL Server" label — adjacent finding, §5.1 |
| E-010 | Code | PodTracerPool.cs, OtlpTarget.cs (full files) | Corroborates E-006: no delay/duration injection anywhere in the emit path |
| E-011 | Code | README.md:37-48 | Canonical diagram-language table used to build the §3.1 capability inventory |
| E-012 | Code | examples.ts:49, 53-101 | Group assignments used for §6.4 imbalance counts |

## 14. PS Integration

- **Artifact path:** `m:/dobri/IF/repos/shoebox/docs/analysis/shoebox-examples-e-001-coverage-gap.md`
- **link-artifact:** Not applicable — the `shoebox` repository has no `scripts/cli.py` / Jerry PS tooling installed (checked: only `Directory.Packages.props`, `LICENSE`, `README.md`, `debug.log`, `src/`, `tests/`, `shoebox.code-workspace` exist at repo root; no `docs/` or `scripts/` directory existed prior to this analysis). This artifact is persisted via direct file write per P-002; a downstream agent with access to the Jerry PS store for this project should register it if this repo is later wired into that tooling.
- **Downstream:** ps-architect (taxonomy decision), ps-validator (define acceptance criteria for whichever new examples get added).
- **Confidence:** High on the redundancy and dead-code findings (static, provable from source); Medium on the FMEA scoring and taxonomy-extensibility judgments (structured argument, not measured data).
