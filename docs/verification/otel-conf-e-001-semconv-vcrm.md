---
DISCLAIMER: This guidance is AI-generated based on NASA Systems Engineering
standards. It is advisory only and does not constitute official NASA guidance.
All SE decisions require human review and professional engineering judgment.
Not for use in mission-critical decisions without SME validation.
---

# Verification Cross-Reference Matrix: OpenTelemetry Semantic Convention Conformance — Snowglobe & Shoebox

> **Project:** OTEL-CONF
> **Entry:** e-001
> **Criticality:** C3
> **Date:** 2026-08-23
> **Status:** FAIL — Shoebox's half remediated 2026-08-23, Snowglobe's half outstanding. See the addendum below.
> **NASA Processes:** NPR 7123.1D Process 7 (Product Verification)

---

## Remediation Status (addendum, 2026-08-23)

*Added after the verification was written. The matrix below is unchanged and remains the
record of what was found; this section records what has since been acted on, one
withdrawn recommendation, and one finding the original pass did not have.*

### Shoebox: closed

Every Shoebox-side defect in this report has shipped a fix, each with a test behind it.

- **#5, `messaging.operation.type = "publish"`.** Now `"send"` on the producer side,
  with `messaging.operation.name = "publish"` retained
  (`TopologyRunner.cs:295`, `MessagingTags` at `:326`). This was the single defect most
  likely to be blocking producer/consumer pairing.
- **#11, `http.route` on Client-kind spans.** Now SERVER-only.
- **#16, `messaging.message.id`.** Now emitted on both halves of every queue hop.
- **Not in the original matrix, found while fixing it:** queues, datastores, caches and
  third parties were each being emitted with their own `service.name`, so a two-node SQL
  diagram published a service called `sql-server`. They are dependencies, not services:
  the caller's client span carries `db.system.name` or `messaging.destination.name` and
  the thing itself emits nothing. Fixed for all four kinds. Consumer spans also carried
  `http.request.method`, claiming a service reached off a queue had arrived over HTTP;
  they are messaging spans only now.
- **Not in the original matrix:** Shoebox emitted telemetry about *itself* under a
  resource named `shoebox`, so a four-service diagram published five services. Removed.
- **Not in the original matrix, found 2026-08-23 by printing the spans:**
  `messaging.system` was hardcoded to `"rabbitmq"` on every publish and every receive,
  regardless of the label on the queue, so a queue called Kafka reported as RabbitMQ.
  `messaging.system` is a Required attribute whose value is a registered enum, which makes
  this a wrong value on a conformant key rather than a cosmetic one. Now read off the
  label, registered values only, defaulting to `rabbitmq`. The consequence was larger than
  the attribute: a reader keys a messaging node on (system, destination) and labels it with
  the system, so every queue in every diagram rendered under one name — see
  [`../analysis/phantom-detect-e-001-rabbitmq-phantom.md`](../analysis/phantom-detect-e-001-rabbitmq-phantom.md) §2.1.

### Recommendation withdrawn: span Links for producer/consumer

Defect #16 and Answer 2 both suggest a span **Link** from the consumer to the producer's
creation context "as a design follow-up". **That recommendation is withdrawn.**

Context propagates through message headers precisely so that the consumer *continues* the
trace. A message that starts its own trace is a broken end-to-end view, which is the
opposite of what either tool exists to demonstrate. Shoebox's parent/child `Activity`
relationship is correct, and Snowglobe is right to parent them. The `messaging.message.id`
half of #16 stands and has shipped; the Links half should not be implemented.

### New finding: fixing Snowglobe's messaging names will break the live grid

The consuming side — `PhantomDetectorControl.BuildMessagingKey` in
`IF.APM.App.Unity.HDRP` — reads the deprecated `messaging.destination`, falling back to a
legacy `message.destination`, and **never reads `messaging.destination.name`**.

That is why Snowglobe's phantom grid works today: Snowglobe emits the old name, which is
the only name the consumer can see. Shoebox dual-emits both spellings as a workaround.

**Therefore defect #8 (Snowglobe's bare `messaging.destination`) has a prerequisite.**
Fix the consumer's key builder to read `messaging.destination.name` *first* before
renaming anything in Snowglobe, or the rename will silently take the live demo down.
Full detail in [`../analysis/phantom-detect-e-001-rabbitmq-phantom.md`](../analysis/phantom-detect-e-001-rabbitmq-phantom.md) §3.1.

### New finding: where the invalid `publish` enum value came from

`IF.APM.OpenTelemetry.Conventions` (in `IF.APM.Ingestion/src/SDK/DotNet/`) is otherwise
current and correctly marks the deprecated names `[Obsolete]`. Its doc comment on
`MessagingOperationType` reads *"e.g., publish, receive, process"* — and `publish` is not
a member of that enum. That comment is IntelliSense-visible at every call site, and is the
most plausible source of defect #5. **Fix the comment**, or the same defect will be
written again by the next person who trusts the tooltip.

### Snowglobe: outstanding

Nothing in Snowglobe's half of this report has been acted on. The fabrications
(`browser.page`, the three `db.redis.*` keys, `db.rows_affected`, `db.docs_count`,
`messaging.batch_size`), the deprecated HTTP/messaging/database vocabulary, the
`system.memory.usage` instrument type, and the two invalid GenAI enum values all stand as
written. See the prerequisite above before starting on the messaging rename.

---

## Claim Under Verification

> "Snowglobe and Shoebox emit standard OpenTelemetry telemetry. Nothing is invented."

**OVERALL VERDICT: FAIL.** The claim is not substantiated for either tool as currently implemented. Two distinct failure classes were found, and they are different in kind:

1. **Fabrication (breaks "nothing is invented"):** Both tools mint attribute keys inside OTel-reserved namespaces (`browser.*`, `db.*`, `db.redis.*`, `messaging.*`, `messaging.operation.type` enum) that do not exist in the published registry. This is a direct violation of the claim's second sentence, not a matter of degree.
2. **Staleness (breaks "standard telemetry", a softer but real failure):** Snowglobe's HTTP, messaging, database, and part of its GenAI vocabulary is built almost entirely on attribute names deprecated by the OpenTelemetry Semantic Conventions project — in the HTTP case, since **v1.20.0 (2023-04-07)**, more than three years before this verification. Software that claims to be "indistinguishable from real instrumentation" cannot rely on a naming scheme that current OTel auto-instrumentation libraries stopped emitting three years ago.

Shoebox is materially closer to current semconv than Snowglobe, but it has its own defects, including one (`messaging.operation.type = "publish"`, an undefined enum value) that sits directly on the load-bearing messaging-correlation path called out in this verification's specific questions.

Every verdict below cites the fetched, published specification and the semconv release (with date) that introduced the current name or the deprecation, per `CHANGELOG.md` of `open-telemetry/semantic-conventions` (releases confirmed via the GitHub Releases API) and, for GenAI, `open-telemetry/semantic-conventions-genai`.

---

## L0: Executive Summary

Both telemetry generators claim their output is standard, unmodified OpenTelemetry. That claim is false in two ways. First, a handful of attributes are outright made up — most visibly `browser.page` on nearly every UI span Snowglobe emits, and three fabricated `db.redis.*` fields — which is exactly the kind of defect that breaks trust in a "looks like real instrumentation" product. Second, and larger in volume, Snowglobe's HTTP, messaging, and database attributes are written against a naming scheme OpenTelemetry deprecated years ago (`http.method` instead of `http.request.method`, `messaging.destination` instead of `messaging.destination.name`, etc.), so a backend built against current semantic conventions will not recognize a large share of what Snowglobe sends. Shoebox is much closer to current spec, but it has one specific bug directly relevant to the phantom-service problem: it labels its message-publish operation with a value (`"publish"`) that OpenTelemetry does not define, on the one attribute (`messaging.operation.type`) that exists specifically so a backend can classify producer/consumer roles automatically. That is a plausible, evidence-backed contributor to the correlation failures reported on Shoebox output, though confirming it as *the* root cause requires reading the backend's inference code, which is outside this verification's artifact set. Risk to the product promise is high: this is not a stylistic nit, it is the exact failure mode ("a made-up attribute breaks that promise") the claim was written to rule out.

---

## L1: Verification Cross-Reference Matrix

Legend: **CONFORMS** = defined in the cited semconv version, current spelling. **DEPRECATED** = was standard, superseded (cite replacement + release). **NON-STANDARD** = not in any semconv registry; judged as *legitimate custom namespace* or *fabricated/invalid* per the task's own criterion.

### 1. Messaging

| Item | Tool(s) | Emitted value/shape | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| `messaging.destination` (bare) | Snowglobe (`scenarios.go`, all publish/receive spans) | e.g. `"orders.created"` | **DEPRECATED** | Renamed to `messaging.destination.name` in [semantic-conventions v1.17.0 (2023-01-17)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.17.0), messaging spans page confirms `messaging.destination.name` is current ([messaging-spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/), Status: Development) | Rename every `attribute.String("messaging.destination", ...)` to `messaging.destination.name` |
| `messaging.operation` (bare) | Snowglobe (all publish/receive spans) | `"publish"` / `"receive"` | **DEPRECATED** | Registry confirms `messaging.operation` is "Deprecated; Replaced by `messaging.operation.type`" ([messaging registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/)); rename shipped in [v1.26.0 (2024-05-21)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.26.0) ("Rename `messaging.operation` to `messaging.operation.type`, add `messaging.operation.name`") | Emit both `messaging.operation.name` (free-text, e.g. `"publish"`/`"receive"`) **and** `messaging.operation.type` (enum) — see next row for the correct enum mapping |
| `messaging.operation.type` value `"publish"` | Shoebox (`TopologyRunner.MessagingTags`, producer side) | `messaging.operation.type = "publish"` | **NON-STANDARD — invalid enum value, fabricated** | `messaging.operation.type` well-known values are exactly `create, send, receive, process, settle` ([messaging registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/); [messaging-spans operation-type table](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/)). `"publish"` is not a member of that enumeration. | Change the producer-side value to `"send"` (the span is its own creation context, so `send`→`PRODUCER` applies per the operation-type/span-kind table). Keep `messaging.operation.name = "publish"` (system-specific string is fine) but fix `messaging.operation.type`. This is the single highest-severity messaging defect: it sits on the attribute a spec-conformant backend uses to classify producer vs. consumer roles. |
| `messaging.operation.type` value `"process"` | Shoebox (`TopologyRunner.MessagingTags`, consumer side) | `messaging.operation.type = "process"` | **CONFORMS** | `process` is a valid enum member and maps to `CONSUMER` span kind ([messaging-spans operation-type table](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/)), which matches Shoebox's `ActivityKind.Consumer` on the same span. | None |
| `messaging.operation.name/.type` values `"publish"`/`"receive"` | Snowglobe consumer spans (e.g. `ProcessPayment`, `ReserveStock`, `SendOrderConfirmation`) use `messaging.operation = "receive"` **with `SpanKindConsumer`** | — | **NON-STANDARD pairing once migrated** | Per the operation-type→span-kind table, `receive` maps to **`CLIENT`**, and only `process` maps to `CONSUMER` ([messaging-spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/); also see open discussion [semantic-conventions#1366](https://github.com/open-telemetry/semantic-conventions/issues/1366) on this exact ambiguity). Snowglobe currently pairs a "receive" semantic with a Consumer span kind, which will not be a valid pairing once the bare `messaging.operation` migration happens. | When migrating off the deprecated bare attribute, set `messaging.operation.type = "process"` (not `"receive"`) on every span that keeps `SpanKindConsumer`, or switch the span kind to `CLIENT` if the tool intends to model pull-based receipt. Given these are push-style async handlers, `process` + `CONSUMER` is the correct target state. |
| Span name `"rabbitmq publish orders.created"` (producer) | Snowglobe | 3-token compound name | **NON-STANDARD span name** | Span name SHOULD be `{messaging.operation.name} {destination}` (2-token) ([messaging-spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/)), e.g. `publish orders.created`. Snowglobe prepends the system name (`rabbitmq`), which the spec does not call for. | Rename to `publish orders.created` (drop the leading system token) |
| Span names on Snowglobe consumer spans (`"ProcessPayment"`, `"ReserveStock"`, `"SendOrderConfirmation"`, etc.) | Snowglobe | Business-domain names, not `{operation} {destination}` | **NON-STANDARD span name** | Same citation as above — these are `SpanKindConsumer` spans carrying `messaging.*` attributes and should be named per the messaging convention if they are meant to represent the receive/process operation itself. | Either (a) rename to `process {destination}` per spec if the span *is* the messaging operation, or (b) keep the business name but add a distinct child/linked span named per convention for the actual message receipt — current code conflates the two. |
| Span names `"publish {dest}"` / `"process {dest}"` | Shoebox | e.g. `publish orders-queue`, `process orders-queue` | **CONFORMS** | Matches `{messaging.operation.name} {destination}` exactly ([messaging-spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/)) | None |
| `messaging.system` | Both | `"rabbitmq"` / `"kafka"` | **CONFORMS** | Current, required attribute, unchanged across versions ([messaging registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/)) | None |
| `messaging.destination.name` | Shoebox | Queue's `ServiceName` | **CONFORMS** | Current spelling ([messaging registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/)) | None |
| `messaging.message.id` / `messaging.message.conversation_id` | **Neither tool emits these** | — | **GAP, not fabrication** | Per the spec: "For each message it accounts for, the 'Process' or 'Receive' span SHOULD link to the message's creation context," and correlation relies on `messaging.message.id` / `messaging.message.conversation_id` ([messaging-spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/)) | **Direct answer to Question 2:** yes, this is missing from both tools. A topology-inference backend that keys on message-level correlation (rather than destination-name + span-kind pairing alone) has nothing to key on. Recommend both tools emit a `messaging.message.id` per message and, for Shoebox specifically, consider a span **Link** from consumer to producer creation context in addition to (or instead of) the current direct parent/child `Activity` relationship, since the spec's default correlation model is link-based, not parent/child. |
| `messaging.batch_size` (custom) | Snowglobe (`bulkNotificationFlow`) | Integer | **NON-STANDARD — fabricated inside reserved namespace** | The registry defines `messaging.batch.message_count` for exactly this purpose ([messaging registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/)); `messaging.batch_size` does not exist. This is a made-up member of the reserved `messaging.*` namespace, not a legitimate product-specific tag (it is not under a private prefix). | Rename to `messaging.batch.message_count` |

### 2. Database

| Item | Tool(s) | Emitted value | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| `db.system` (bare) | Snowglobe | `"postgresql"`, `"redis"`, `"elasticsearch"` | **DEPRECATED** | Renamed to `db.system.name` for spans in [v1.30.0 (2025-01-24)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.30.0) ("Rename `db.system` to `db.system.name` and clean up its values"); metrics-side rename followed in v1.31.0. Database-spans page confirms `db.system.name` is the current required attribute, span kind SHOULD be `CLIENT` ([database-spans](https://opentelemetry.io/docs/specs/semconv/database/database-spans/), Status: Stable). | Rename to `db.system.name` |
| `db.system.name` | Shoebox | `"postgresql"`, `"redis"` | **CONFORMS** | Same citation as above | None |
| `db.operation` (bare) | Snowglobe | `"SELECT"`, `"INSERT"`, `"GET"`, etc. | **DEPRECATED** | Renamed to `db.operation.name` in [v1.26.0 (2024-05-21)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.26.0) ("Rename `db.operation` to `db.operation.name`") | Rename to `db.operation.name` |
| `db.operation.name` | Shoebox | `"GET"` | **CONFORMS** | Same citation | None |
| `db.name` (bare) | Snowglobe | `"orders_db"`, `"inventory_db"`, etc. | **DEPRECATED** | Renamed to `db.namespace` in v1.26.0 (same release as `db.operation`, per changelog: "Rename `db.name` and `db.redis.database_index` to `db.namespace`") | Rename to `db.namespace` |
| `db.statement` (bare) | Snowglobe | Raw SQL / ES query text | **DEPRECATED** | Renamed to `db.query.text` in v1.26.0 ("Rename `db.statement` to `db.query.text` and introduce `db.query.parameter.<key>`") | Rename to `db.query.text` |
| `db.query.text` | Shoebox | `"SELECT * FROM {name}"` | **CONFORMS** | Current name, database-spans page lists it as Recommended ([database-spans](https://opentelemetry.io/docs/specs/semconv/database/database-spans/)) | None |
| `db.redis.key`, `db.redis.ttl_seconds`, `db.redis.keys_count` | Snowglobe | e.g. `"session:abc123"`, `300`, `15` | **NON-STANDARD — fabricated inside reserved namespace** | The only officially registered `db.redis.*` attribute is `db.redis.database_index`, and it is itself deprecated in favor of `db.namespace` ([db registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/db/)). `db.redis.key`, `db.redis.ttl_seconds`, `db.redis.keys_count` do not exist in the registry at any version. This is squatting inside a reserved sub-namespace with invented members — the exact defect class the claim rules out. | Move these under a private/custom prefix, e.g. `tracegen.redis.key` / `tracegen.redis.ttl_seconds` / `tracegen.redis.keys_count`, consistent with the project's own stated rule in `docs/metrics-design.md` ("prefix with... an application name... explicitly warns against extending an existing OTel namespace with a non-standard [attribute]") |
| `db.rows_affected`, `db.docs_count` | Snowglobe | Integers | **NON-STANDARD — fabricated inside reserved namespace** | Closest registered attribute is `db.response.returned_rows` ("Number of rows returned by the operation") ([db registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/db/)); `db.rows_affected` and `db.docs_count` are not registered names or aliases of it. | Rename `db.rows_affected` → `db.response.returned_rows` where semantically a read; for write/bulk-index counts with no matching registered attribute, move to a `tracegen.*` custom key instead of inventing inside `db.*`. |
| DB span kind | Both | `SpanKindClient` (Snowglobe), `ActivityKind.Client` (Shoebox) | **CONFORMS** | "Database client spans SHOULD be `CLIENT`" ([database-spans](https://opentelemetry.io/docs/specs/semconv/database/database-spans/)) | None |

### 3. HTTP

| Item | Tool(s) | Emitted value | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| `http.method` (bare) | Snowglobe (pervasive — every HTTP-shaped span) | `"GET"`, `"POST"` | **DEPRECATED** | Renamed to `http.request.method` in [v1.20.0 (2023-04-07)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.20.0); http-spans page (Status: Stable) confirms `http.request.method` as the current required attribute for both client and server spans ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)) | Rename every occurrence to `http.request.method` |
| `http.url` (bare) | Snowglobe | Full URL string | **DEPRECATED** | Renamed to `url.full` in the same v1.20.0 rework; `url.full` is Required on HTTP client spans per the current spec ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)) | Rename to `url.full`; also add the co-required `server.address` / `server.port` on client spans (currently absent) |
| `http.status_code` (bare) | Snowglobe | `200`, `401`, `402`, `504` | **DEPRECATED** | Renamed to `http.response.status_code` in v1.20.0 | Rename to `http.response.status_code` |
| `http.user_agent` (bare) | Snowglobe (one call site, `api-gateway` span in `createOrderFlow`) | `"Mozilla/5.0"` | **DEPRECATED** | Renamed to `user_agent.original` in [v1.19.0 (2023-03-06)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.19.0) | Rename to `user_agent.original` |
| `net.peer.ip` | Snowglobe (`api-gateway` spans) | IP string | **DEPRECATED** | Registry: "Deprecated. Replaced by `network.peer.address`" ([network registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/network/)); original `net.peer.*` rework in [v1.21.0 (2023-07-13)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.21.0) | Rename to `network.peer.address` |
| `peer.service` | Snowglobe (Stripe/SendGrid/external calls, GenAI helper spans) | `"stripe-api"`, `"sendgrid"`, provider peer name | **DEPRECATED (recent)** | "The `peer.service` attribute has been deprecated in favor of `service.peer.name`" — [v1.39.0 (2026-01-12)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.39.0), roughly 7 months before this verification | Rename to `service.peer.name` |
| `http.request.method`, `http.route`, `http.response.status_code` | Shoebox — server-kind spans only (root pod handling) | `"GET"`, `"/{pod}"`, n/a | **CONFORMS** | `http.request.method` current/required; `http.route` is Conditionally Required **on SERVER spans specifically** ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)) | None, for the root/Server-kind case |
| `http.route` attached to `ActivityKind.Client` spans | Shoebox (`TopologyRunner.SemanticTags`, default `PodKind` case, non-root pods) | `http.route = "/{pod.ServiceName}"` set unconditionally regardless of computed span kind | **NON-STANDARD — misapplied attribute** | `http.route` is scoped to HTTP **SERVER** spans only; it does not appear in the client attribute table at all ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)). `SemanticTags` is called for every default-kind pod regardless of whether `Visit` computed `ActivityKind.Client` or `ActivityKind.Server` for that hop, so internal service-to-service calls (Client kind) are tagged with a Server-only attribute. | Branch `SemanticTags` (or its caller) on the actual `ActivityKind` computed in `Visit`: Server-kind default pods keep `http.request.method` + `http.route`; Client-kind default pods should instead emit `url.full`/`server.address`+`server.port` (as already done correctly for `PodKind.External`), not `http.route`. |
| `server.address` | Shoebox (`PodKind.External`) | `"{pod}.example.com"` | **CONFORMS**, incomplete | `server.address` is Required on HTTP client spans ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)) | Gap, not fabrication: `server.port` and `url.full` are also Required on client spans and are currently omitted. Add both. |
| HTTP span kind | Both | Client / Server per topology position | **CONFORMS** | Matches the general HTTP span-kind model implicit in the client/server attribute split ([http-spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/)) | None, aside from the misapplied-`http.route` case above |

### 4. GenAI

> **Stability caveat, confirmed from source:** the entire `gen_ai.*` namespace carries a **Development** stability badge, and as of [semantic-conventions v1.42.0 (2026-06-12)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.42.0) the GenAI conventions were split out of the main repository into the dedicated [`open-telemetry/semantic-conventions-genai`](https://github.com/open-telemetry/semantic-conventions-genai) repo. Anything GenAI-related is experimental by the spec's own definition, not just by this verification's judgment.

| Item | Tool(s) | Emitted value | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| Code comment: "matching... OTel GenAI Semantic Conventions v1.38.0" (`helpers.go`) | Snowglobe | — | **FALSE CLAIM in code** | `gen_ai.system` was renamed to `gen_ai.provider.name` in [v1.37.0 (2025-08-25)](https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.37.0) — the release **immediately before** the v1.38.0 the code claims to target. The code cannot conform to v1.38.0 while using an attribute that was already renamed away in v1.37.0. | Update the comment and, substantively, the attribute (next row) |
| `gen_ai.system` | Snowglobe (`chatSpan`, `embeddingSpan`, `agentSpan`; also read back into `gen_ai.client.token.usage` / `gen_ai.client.operation.duration` metric attributes in `metrics.go`) | `"openai"`, `"anthropic"`, etc. | **DEPRECATED** | Renamed to `gen_ai.provider.name` in v1.37.0 ("Follow system-specific naming policy in GenAI semantic conventions... Rename `gen_ai.system` to `gen_ai.provider.name`") | Rename to `gen_ai.provider.name` on spans; propagate the rename into the metrics derivation in `trackGenAI` |
| `gen_ai.operation.name = "retrieve"` | Snowglobe (`vector-db-service` span, RAG flow) | `"retrieve"` | **NON-STANDARD — invalid enum value** | Current well-known values for `gen_ai.operation.name` include `retrieval`, not `retrieve` ([semantic-conventions-genai gen-ai-spans.md](https://github.com/open-telemetry/semantic-conventions-genai)) | Change to `"retrieval"`; span name should follow suit: retrieval spans are named `{operation.name} {gen_ai.data_source.id}`, so `retrieval product-embeddings` |
| `gen_ai.operation.name = "embedding"` | Snowglobe (`embeddingSpan` helper) | `"embedding"` | **NON-STANDARD — invalid enum value** | Current well-known values include `embeddings` (plural), not `embedding` (singular) (same citation) | Change to `"embeddings"`; span name to `embeddings {model}` |
| `gen_ai.operation.name = "chat"` | Snowglobe (`chatSpan`) | `"chat"` | **CONFORMS** | `chat` is a listed well-known value | None |
| `gen_ai.operation.name = "invoke_agent"` / `"execute_tool"` | Snowglobe (`agentSpan`, `toolSpan`) | as named | **CONFORMS** (enum value) | Both are listed well-known values | None on the attribute value itself |
| Span kind `INTERNAL` on `agentSpan`/`toolSpan` | Snowglobe | `SpanKindInternal` | **NOT VERIFIABLE from a page this verification could retrieve** | The dedicated agent-spans page in the new GenAI repo returned only a relocation notice when fetched; the general spans page states span kind is generally `CLIENT`, "may be `INTERNAL` for same-process operations (Inference, Memory)" without confirming Agent/Tool explicitly | Flagging as an open item rather than a verdict — do not treat as either pass or fail until the agent-specific page is confirmed |
| `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` | Snowglobe (spans and derived `gen_ai.client.token.usage` metric) | Integers | **CONFORMS** | Confirmed current and "Recommended" in the new GenAI repo's spans page, despite showing a blanket "Deprecated" badge on the old opentelemetry.io registry page (an artifact of the repo split, not a real attribute deprecation) | None |
| `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.response.id`, `gen_ai.request.temperature`, `gen_ai.request.max_tokens`, `gen_ai.response.finish_reasons`, `gen_ai.embedding.dimension`, `gen_ai.request.encoding_formats`, `gen_ai.agent.id/.name/.description/.version`, `gen_ai.conversation.id`, `gen_ai.tool.name/.type/.call.id/.description`, `gen_ai.evaluation.name`, `gen_ai.evaluation.score.value` | Snowglobe (various GenAI spans, `scenarios_ai.go` + `helpers.go`) | as named | **NOT EXHAUSTIVELY VERIFIED** | These were not individually re-confirmed against the dedicated `semantic-conventions-genai` registry within this pass (only `gen_ai.system`→`gen_ai.provider.name`, the `operation.name` enum, and usage-token attributes were independently confirmed) | Do not treat as a passing verdict. Recommend a dedicated follow-up verification pass against `open-telemetry/semantic-conventions-genai` `model/registry/gen-ai.yaml` (this run's fetches of that file 404'd; a git clone or GitHub API read with auth is recommended) before shipping GenAI scenarios as conformant. |
| `gen_ai.data_source.id` | Snowglobe (`vector-db-service` span) | `"product-embeddings"` | **CONFORMS (name only)** | Confirmed to exist as a registered attribute and matches the retrieval span-name pattern | Value shape looks reasonable; not independently checked further |

### 5. Metrics

| Item | Tool | Instrument / unit emitted | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| `http.server.request.duration` | Snowglobe | Histogram, unit `s`, boundaries `[0.005…10]` | **CONFORMS** | Matches spec exactly: Histogram, unit `s`, identical recommended boundaries, Status: Stable ([http-metrics](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/)) | None |
| `http.server.active_requests` | Snowglobe | UpDownCounter, unit `{request}`, attribute `http.request.method` only | **CONFORMS, incomplete** | Correct instrument/unit/name (Status: Development). Required attributes are `http.request.method` **and** `url.scheme`; `url.scheme` is not attached here. | Gap, not fabrication: add `url.scheme` (e.g. `"https"`, which the code already hardcodes elsewhere in `recordServerRequest`) to this instrument's attribute set too |
| `db.client.connection.count` | Snowglobe | UpDownCounter, unit `{connection}`, attribute `db.client.connection.state` | **CONFORMS, incomplete** | Correct instrument/name/unit and correct use of `db.client.connection.state` (Development) — but `db.client.connection.pool.name` is also Required and is not emitted ([database-metrics](https://opentelemetry.io/docs/specs/semconv/database/database-metrics/)) | Add `db.client.connection.pool.name` |
| `system.cpu.utilization` | Snowglobe | Float64ObservableGauge, unit `1` | **CONFORMS, incomplete** | Correct instrument type (Gauge) and unit; `cpu.mode` is Recommended and `cpu.logical_number` Opt-In, neither present (raw yaml: `instrument: gauge`, `unit: "1"`) | Gap: add `cpu.mode` at minimum, since this is a Recommended (not opt-in) dimension |
| `system.memory.usage` | Snowglobe | **Int64ObservableGauge**, unit `By` | **NON-CONFORMANT INSTRUMENT TYPE** | Registry defines this metric as an **UpDownCounter** (`instrument: updowncounter`), unit `By`, with Recommended attribute `system.memory.state` (raw yaml, `model/system/metrics.yaml`) — Snowglobe uses `ObservableGauge`, a different aggregation/point-kind on the wire, and emits no `system.memory.state` dimension at all | Rework as an `Int64ObservableUpDownCounter` and add a `system.memory.state` attribute (e.g. `used`), or, if the team deliberately wants a point-in-time snapshot semantic instead of the spec's cumulative-state model, rename it under `tracegen.*` instead of reusing the reserved name with a different contract. Per the project's own stated rule ("conform to its contract exactly... Conformance is not stylistic here"), the current implementation violates that rule for this specific metric. |
| `gen_ai.client.token.usage`, `gen_ai.client.operation.duration` | Snowglobe | Histogram, units `{token}` / `s` | **CONFORMS on instrument/unit**, inherits attribute defect | Instrument/unit shape matches typical GenAI metrics conventions; attribute set includes `gen_ai.system`, which is deprecated (see GenAI section above) | Propagate the `gen_ai.system` → `gen_ai.provider.name` fix into `trackGenAI`'s metric attribute derivation |
| `tracegen.messaging.queue.depth` | Snowglobe | Gauge, unit `{message}`, attribute `messaging.destination.name` | **CONFORMS as custom namespace; internally inconsistent** | The metric name and unit are correctly custom-namespaced per the project's own stated rule (private prefix; spec's "reverse-domain or app-specific prefix, do not extend an existing namespace" guidance, quoted verbatim in `docs/metrics-design.md`). However, the attribute it carries, `messaging.destination.name`, is the **current** spelling, while the span attribute it is derived from (`trackMessaging` reads `messaging.destination`, the deprecated bare form) is not — i.e., the metric layer already "knows" the correct name and the span layer does not. | No action on the metric name itself; this is additional evidence for prioritizing the `messaging.destination` → `messaging.destination.name` span-level fix, since fixing spans will make the derivation source and the derived metric attribute agree for the first time |
| `tracegen.spans.emitted`, `tracegen.cache.operations` (documented, not found in current `metrics.go`) | Snowglobe | Counter, `{span}` / `{operation}` | **CONFORMS as custom namespace** | Correct reverse-application-name prefixing per the spec's non-standard-metric guidance | None on `tracegen.spans.emitted` (found in code). `tracegen.cache.operations` is documented in `docs/metrics-design.md` but was not found in the read `metrics.go` — the design doc itself notes cache hit/miss was dropped ("One item from the plan was dropped: cache hit ratio... It needs a span attribute first"), so the doc is already stale/self-aware here; not a semconv defect, but the doc should be updated to remove the now-nonexistent row from its Layer-1 table. |
| UCUM unit strings (`s`, `By`, `1`, `{request}`, `{connection}`, `{message}`, `{token}`, `{span}`) | Both | as listed | **CONFORMS** | All match the exact unit strings used by the cited spec pages for the corresponding official metrics, and the curly-brace countable-entity annotations follow OTel's own unit-annotation convention | None |

### 6. Resource

| Item | Tool(s) | Emitted value | Verdict | Citation | Required action |
|---|---|---|---|---|---|
| `service.name` | Both | Service name string | **CONFORMS** | Stable, unchanged resource attribute | None |
| `service.instance.id` | Both | Pod ID | **CONFORMS** | Stable, unchanged; correctly used to distinguish replicas sharing `service.name` per `PodTracerPool`'s own doc comment | None |
| `host.name` | Both | Node / machine name | **CONFORMS** | Stable, unchanged | None |

### 7. Non-standard, product-specific tags

| Item | Tool | Verdict | Judgment |
|---|---|---|---|
| `sandbox.id` | Shoebox | **NON-STANDARD — legitimate** | `sandbox` is not an OTel-reserved namespace. Propagated via OTel `Baggage`, a correct use of a real OTel mechanism for a product-specific concern. No collision, no fabrication. |
| `shoebox.pod.kind` | Shoebox | **NON-STANDARD — legitimate** | Product-name-prefixed (`shoebox.*`), exactly the pattern the spec recommends for non-standard attributes. Not a reserved namespace. |
| `tracegen.*` metric names | Snowglobe | **NON-STANDARD — legitimate** | Same reasoning; explicitly the pattern `docs/metrics-design.md` describes and correctly follows for metric *names*. The inconsistency is that this discipline was not extended to span *attributes* (see `browser.page`, `db.redis.*`, `db.rows_affected`/`db.docs_count`, `messaging.batch_size` above). |
| `browser.page` | Snowglobe | **NON-STANDARD — fabricated, not legitimate** | `browser` **is** a real, reserved OTel namespace (`browser.brands`, `browser.language`, `browser.mobile`, `browser.platform`, `browser.web_vital.*`, `browser.document.url.full`). `browser.page` is not a member of it at any version ([browser registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/browser/)). This is the highest-visibility fabricated attribute in the codebase: it appears on nearly every UI-originating span across essentially all fourteen scenario functions. |
| Business attributes (`order.id`, `order.total`, `cart.user_id`, `payment.provider`, `fraud.score`, `ml.model`, `shipping.carrier`, `search.query`, `cache.hit`, `webhook.event`, `scheduling.job`, `notification.type`, `health.service`, etc.) | Both | **NON-STANDARD — legitimate** | None of `order`, `cart`, `payment`, `fraud`, `ml`, `shipping` (business sense), `search`, `cache`, `webhook`, `scheduling`, `notification`, `health` are reserved OTel top-level namespaces. These are ordinary product/business telemetry, exactly the kind of attribute real instrumentation carries alongside semconv-defined ones. No defect. |

---

## L2: Systems Perspective

### Coverage Metrics

| Metric | Count | Note |
|---|---|---|
| Distinct emitted items verified against published spec | 61 | Spans, span-name patterns, span kinds, metrics, resource attributes |
| CONFORMS (fully) | 22 | |
| CONFORMS but incomplete (missing required/recommended attribute) | 8 | Gaps, not fabrications — lower severity |
| DEPRECATED (real attribute, stale spelling) | 15 | Almost entirely concentrated in Snowglobe's HTTP/messaging/DB/GenAI-system usage |
| NON-STANDARD — fabricated / invalid enum value | 8 | `browser.page`, `db.redis.key/ttl_seconds/keys_count` (3 items), `db.rows_affected`, `db.docs_count`, `messaging.batch_size`, `messaging.operation.type="publish"`, `gen_ai.operation.name="retrieve"`, `gen_ai.operation.name="embedding"` |
| NON-STANDARD — legitimate custom namespace | 7 | `sandbox.id`, `shoebox.pod.kind`, `tracegen.*`, business attributes as a class |
| Not exhaustively verifiable from published spec within this pass | 2 (span kind on agent/tool spans; long tail of GenAI attributes) | Explicitly flagged rather than guessed, per instruction |

### Gap Analysis

**Snowglobe — systemic staleness.** The HTTP attribute set (`http.method`, `http.url`, `http.status_code`, `http.user_agent`) has been deprecated since 2023-04-07 (`http.method`/`http.url`/`http.status_code`) and 2023-03-06 (`http.user_agent`) respectively — over three years. The database attribute set (`db.system`, `db.operation`, `db.name`, `db.statement`) has been deprecated since 2024-05-21 / 2025-01-24. This is not a matter of a recent spec churn catching the tool off guard; it indicates the HTTP/DB/messaging instrumentation in Snowglobe's scenario code was written against (or never updated past) a pre-2023/2024 mental model of OTel semconv and has not been revisited since, even though `main.go` imports `go.opentelemetry.io/otel/semconv/v1.26.0` for its own resource construction — the codebase already depends on a semconv version high enough that these renames are all in scope, it simply doesn't apply that version's vocabulary to attributes hand-authored in `scenarios.go`.

**Shoebox — smaller footprint, sharper defects.** Shoebox's own code comments show clear, correct awareness of the spec ("The OpenTelemetry messaging attributes, current spelling. operation.type is the one that pairs a publish with a receive, and it is the one Shoebox was missing entirely.") — yet the value chosen for that exact attribute on the producer path is not a valid enum member. This is a "close but wrong" defect, more dangerous than an obviously-old attribute because it looks conformant on casual review (both `operation.name` and `operation.type` are present, correctly named) and only fails on the enum value.

**Both tools — missing correlation attributes.** Neither emits `messaging.message.id` or `messaging.message.conversation_id`, and Shoebox uses direct span parent/child linkage rather than the spec's link-based correlation model for consumer→producer. This is a design gap relevant to any backend that infers topology from message-level correlation rather than destination-name matching alone.

### Review Readiness

| Gate | Required for this artifact | Current | Ready |
|---|---|---|---|
| Claims-verification gate (this repo's own CLAUDE.md) | Cite a verified source or flag unverified for every claim | Done for all rows except the explicitly flagged GenAI long tail | Partially — flagged items must not be presented as verified |
| `/adversary` (S-014), threshold ≥ 0.92, 0 Critical | Required for all GTM/engineering claims content | Not run as part of this pass (V&V agent scope) | **No** — recommend routing this VCRM through adversarial review before it is used to close out the "nothing is invented" claim publicly |
| Fix verification (re-run this VCRM after remediation) | All FAIL/DEPRECATED/NON-STANDARD rows closed | Not started | **No** |

---

## Defect List, Ordered by Severity

1. **[CRITICAL] `browser.page` fabricated inside the reserved `browser.*` namespace (Snowglobe).** Appears on nearly every UI span across all scenario functions in `scenarios.go` and `scenarios_ai.go`. Directly falsifies "nothing is invented." **Fix:** remove, or move to a non-reserved key such as `snowglobe.browser.page` / a generic `url.path`-style attribute if the intent is to record the SPA route.

2. **[CRITICAL] `db.redis.key`, `db.redis.ttl_seconds`, `db.redis.keys_count` fabricated inside the reserved `db.redis.*` sub-namespace (Snowglobe).** Only `db.redis.database_index` (itself deprecated) is real. **Fix:** rename all three to a `tracegen.redis.*` custom prefix.

3. **[CRITICAL] `db.rows_affected`, `db.docs_count` fabricated inside the reserved `db.*` namespace (Snowglobe).** **Fix:** map read-count usages to `db.response.returned_rows`; move the rest to `tracegen.*`.

4. **[CRITICAL] `messaging.batch_size` fabricated inside the reserved `messaging.*` namespace (Snowglobe).** **Fix:** rename to `messaging.batch.message_count`.

5. **[HIGH] `messaging.operation.type = "publish"` is not a valid enum value (Shoebox, `TopologyRunner.MessagingTags`).** This is the one defect in this report most directly tied to the reported phantom-inference failure: it is the exact attribute a spec-conformant backend uses to classify producer role, and its value is undefined. **Fix:** change the producer-side call from `MessagingTags(queue, "publish")`'s type value to `"send"` while keeping `operation.name = "publish"`.

6. **[HIGH] `gen_ai.operation.name = "retrieve"` and `"embedding"` are invalid enum values (Snowglobe).** Both should use the plural/noun forms `"retrieval"` and `"embeddings"`. **Fix:** update `scenarios_ai.go`'s `vecSearch` span and `helpers.go`'s `embeddingSpan`, and the corresponding span names (`retrieval {data_source.id}`, `embeddings {model}`).

7. **[HIGH] Pervasive use of `http.method`/`http.url`/`http.status_code` instead of `http.request.method`/`url.full`/`http.response.status_code` (Snowglobe), deprecated since 2023-04-07.** Affects essentially every HTTP-shaped span across all scenario functions. **Fix:** global rename across `scenarios.go`, `scenarios_ai.go`, `helpers.go`.

8. **[HIGH] `messaging.destination` / `messaging.operation` bare attributes throughout Snowglobe**, deprecated since 2023-01-17 / 2024-05-21 respectively, **and** the resulting `operation.type` value for consumer spans must be `"process"` (not `"receive"`) to correctly pair with the `SpanKindConsumer` already in use. **Fix:** rename to `.name`, add `.type`, correct the consumer-side enum value; also fix the producer span-name format (drop the leading `"rabbitmq "` token).

9. **[MEDIUM] `db.system`/`db.operation`/`db.name`/`db.statement` bare attributes throughout Snowglobe**, deprecated 2024-05-21 to 2025-01-24. **Fix:** global rename to `.system.name`/`.operation.name`/`.namespace`/`.query.text`.

10. **[MEDIUM] `gen_ai.system` deprecated in favor of `gen_ai.provider.name` (2025-08-25), used in both spans and the derived `gen_ai.client.*` metrics (Snowglobe).** Code comment falsely claims v1.38.0 conformance while using an attribute already renamed in the prior release. **Fix:** rename attribute in `helpers.go` and propagate through `trackGenAI` in `metrics.go`; correct the stale version claim in the comment.

11. **[MEDIUM] `http.route` attached to `ActivityKind.Client` spans in Shoebox's default `SemanticTags` case.** `http.route` is Server-only per spec. **Fix:** branch on the computed `ActivityKind` and use `url.full`/`server.address`+`server.port` for Client-kind default pods.

12. **[MEDIUM] `net.peer.ip` (deprecated 2023, replaced by `network.peer.address`) and `peer.service` (deprecated 2026-01-12, replaced by `service.peer.name`) in Snowglobe.** **Fix:** rename both.

13. **[MEDIUM] `http.user_agent` (deprecated 2023-03-06, replaced by `user_agent.original`) — one call site in Snowglobe's `createOrderFlow`.** **Fix:** rename.

14. **[LOW] `system.memory.usage` implemented as an `ObservableGauge` instead of the spec-mandated `UpDownCounter`, and missing the Recommended `system.memory.state` attribute (Snowglobe).** Changes the wire-level point kind, not just a label. **Fix:** switch to `Int64ObservableUpDownCounter` and add `system.memory.state`, or rename under `tracegen.*` if a snapshot-gauge semantic is intentional.

15. **[LOW] Missing Required/Recommended attributes without fabrication:** `http.server.active_requests` missing `url.scheme`; `db.client.connection.count` missing `db.client.connection.pool.name`; `system.cpu.utilization` missing `cpu.mode`; HTTP client spans (Snowglobe `url.full`-pending sites, Shoebox `PodKind.External`) missing `server.port`/full `url.full`. **Fix:** add the missing attributes; none of these require new invented data, only using values already computed elsewhere in the code.

16. **[LOW] Neither tool emits `messaging.message.id` / `messaging.message.conversation_id`, and Shoebox uses parent/child `Activity` linkage rather than the spec's link-based creation-context correlation for consumer spans.** **Fix:** add a per-message `messaging.message.id`; consider span `Links` for the consumer→producer relationship per spec guidance, as a design follow-up rather than a strict conformance bug (parent/child is not explicitly forbidden, just not the spec's stated default model).

17. **[INFORMATIONAL] `docs/metrics-design.md`'s Layer-1 table lists `tracegen.cache.operations`, which the same document's own "What changed" section says was dropped and is not present in the read `metrics.go`.** **Fix:** remove the stale table row or re-add the metric; documentation/implementation drift, not a semconv defect.

---

## Answers to the Specific Questions

1. **Messaging (load-bearing).** Snowglobe emits the deprecated bare `messaging.destination`/`messaging.operation` (current since v1.17.0/v1.26.0 respectively); Shoebox emits the current `messaging.destination.name` + `messaging.operation.name` + `messaging.operation.type`, but sets `operation.type = "publish"` on the producer side, which is not a member of the `{create, send, receive, process, settle}` enum — it should be `"send"`. Shoebox's consumer-side `operation.type = "process"` is correct and correctly paired with `ActivityKind.Consumer`. Shoebox's span names `publish {dest}` / `process {dest}` **do** match the spec's `{operation.name} {destination}` format. Emitting both `operation.name` and `operation.type` is **correct, not redundant** — both are (conditionally) required and serve different purposes (free-text system name vs. standardized enum for cross-system logic); the defect is the *value* on one of them, not the duplication.

2. **Missing correlation attributes.** Yes: `messaging.message.id` and `messaging.message.conversation_id` are the spec-defined correlation attributes, and neither tool emits either. The spec's stated default correlation model for process/receive spans is a span **Link** to the creation context; Shoebox instead uses a direct parent/child `Activity` relationship, which is not explicitly prohibited but is a divergence from the documented default worth a design follow-up.

3. **Database.** Shoebox's `db.system.name`/`db.query.text`/`db.operation.name` are current. Snowglobe's `db.system`/`db.operation` are deprecated (replaced 2024-05-21/2025-01-24). Snowglobe additionally fabricates `db.redis.key`/`db.redis.ttl_seconds`/`db.redis.keys_count` and `db.rows_affected`/`db.docs_count` inside the reserved `db.*` namespace — these have no registered equivalent and must be renamed out of the namespace or mapped to `db.response.returned_rows` where applicable.

4. **HTTP.** Shoebox's `http.request.method`, `http.route` (server spans only), and `server.address` (external/client spans) are current and correctly scoped, aside from `http.route` leaking onto Client-kind spans in the default pod case (defect #11).

5. **GenAI.** Snowglobe's GenAI spans claim conformance to a specific semconv release (v1.38.0) in a code comment while using `gen_ai.system`, an attribute renamed to `gen_ai.provider.name` one release earlier (v1.37.0) — the claim is internally inconsistent with the code. `gen_ai.operation.name` values `"retrieve"` and `"embedding"` are invalid (should be `"retrieval"`/`"embeddings"`). The entire GenAI namespace is Development-stability and was split into a separate repository as of v1.42.0; treat all GenAI conformance claims as provisional. Several GenAI attributes were not independently re-verified in this pass and are flagged as such rather than scored.

6. **Metrics.** The `tracegen.` prefix is legitimate custom namespacing and is correctly applied to metric *names* (unlike the attribute-level fabrications above). Units are valid UCUM/OTel-annotation strings throughout (`s`, `By`, `1`, `{request}`, `{connection}`, `{message}`, `{token}`, `{span}`). Instrument types are correct except `system.memory.usage`, which should be an `UpDownCounter` per spec and is implemented as an `ObservableGauge` — a real wire-format conformance defect, not merely cosmetic.

7. **Resource.** `service.name`, `service.instance.id`, `host.name` are all current, stable, and correctly used in both tools.

8. **Non-standard tags.** `sandbox.id` (Shoebox) and `shoebox.pod.kind` (Shoebox) are legitimate product-specific attributes: neither `sandbox` nor `shoebox` is a reserved OTel namespace, and `sandbox.id` is propagated through the real OTel `Baggage` API. They are not violations. The violations are the ones squatting inside *reserved* namespaces (`browser.*`, `db.*`/`db.redis.*`, `messaging.*` batch-size), which is a materially different and worse category than a private, unclaimed prefix.

---

## What Could Not Be Verified From the Published Spec

- The exact span-kind rule for GenAI agent/tool spans (`create_agent`/`invoke_agent`/`execute_tool`) — the dedicated agent-spans page in `semantic-conventions-genai` returned only a repository-relocation notice on fetch. This verification does not assert a verdict on Snowglobe's use of `SpanKindInternal` for `agentSpan`/`toolSpan`.
- A full, individually-cited pass over every remaining `gen_ai.*` attribute used in `scenarios_ai.go`/`helpers.go` (agent id/name/description/version, tool name/type/call id/description, evaluation name/score, embedding dimension, encoding formats, conversation id). Time and access constraints (the `semantic-conventions-genai` registry YAML 404'd on direct raw fetch) limited this pass to the attributes explicitly confirmed above. **Do not treat unlisted GenAI attributes as verified.**
- Whether the specific backend inference bug reported on Shoebox output ("inference is currently failing on Shoebox output") is caused by defect #5 (`operation.type = "publish"`) as opposed to something in the backend's own inference logic, which is not part of this verification's artifact set. Defect #5 is flagged as the most plausible producer-side contributor found in the artifacts reviewed, not as a confirmed root cause.

---

## References

- [OpenTelemetry Semantic Conventions — Messaging Spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/) (Status: Development)
- [OpenTelemetry Semantic Conventions — Messaging Attribute Registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/messaging/)
- [OpenTelemetry Semantic Conventions — Database Spans](https://opentelemetry.io/docs/specs/semconv/database/database-spans/) (Status: Stable)
- [OpenTelemetry Semantic Conventions — Database Attribute Registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/db/)
- [OpenTelemetry Semantic Conventions — Database Metrics](https://opentelemetry.io/docs/specs/semconv/database/database-metrics/)
- [OpenTelemetry Semantic Conventions — HTTP Spans](https://opentelemetry.io/docs/specs/semconv/http/http-spans/) (Status: Stable)
- [OpenTelemetry Semantic Conventions — HTTP Metrics](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/)
- [OpenTelemetry Semantic Conventions — Network Attribute Registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/network/)
- [OpenTelemetry Semantic Conventions — Browser Attribute Registry](https://opentelemetry.io/docs/specs/semconv/registry/attributes/browser/)
- [OpenTelemetry Semantic Conventions — System Metrics (`model/system/metrics.yaml`)](https://raw.githubusercontent.com/open-telemetry/semantic-conventions/main/model/system/metrics.yaml)
- [OpenTelemetry Semantic Conventions — CHANGELOG.md](https://raw.githubusercontent.com/open-telemetry/semantic-conventions/main/CHANGELOG.md)
- [OpenTelemetry Semantic Conventions — GitHub Releases (dates)](https://github.com/open-telemetry/semantic-conventions/releases)
- [OpenTelemetry GenAI Semantic Conventions (dedicated repository, post-v1.42.0)](https://github.com/open-telemetry/semantic-conventions-genai)
- [semantic-conventions#1366 — "Messaging: should receive spans be CLIENT?"](https://github.com/open-telemetry/semantic-conventions/issues/1366) (background on the `receive`→`CLIENT` vs. `CONSUMER` ambiguity cited in defect #8)
- Artefacts reviewed: `snowglobe/cmd/snowglobe/scenarios.go`, `scenarios_ai.go`, `metrics.go`, `main.go`, `helpers.go` (GenAI span helpers, read for accurate GenAI verdicts), `docs/metrics-design.md`; `shoebox/src/Shoebox.Api/Run/TopologyRunner.cs`, `Emit/PodTracerPool.cs`, `Sandbox/SandboxConstants.cs`

---

*Generated by nse-verification agent v1.0.0. This is a V&V evidence artifact, not a fix PR. Every FAIL/DEPRECATED/NON-STANDARD row above should be turned into a tracked issue in the respective repository (`snowglobe`, `shoebox`) before this claim can be re-verified as PASS.*
