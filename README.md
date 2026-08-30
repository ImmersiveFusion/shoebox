![An open box holding a small glowing system on a grid, one connection broken and sparking](.img/banner.jpg)

# Shoebox

**A small system you can break.**

Paste a Mermaid diagram of a system, break something in it, and fire one request.
Real OpenTelemetry comes out the other side. No account, no install, nothing to
deploy.

It is **fully synthetic**. There is no database, no cache and no real
infrastructure behind it. The diagram is the whole state, which is what makes it
safe to hand to a stranger and cheap to run.

![Shoebox showing a pasted diagram, the rendered topology, and the result of one fired request](.img/screenshot.png)

## What it does

1. **Paste a diagram.** Mermaid flowchart in, topology rendered.
2. **Break a call.** A label on an edge is all it takes.
3. **Fire one request.** Nothing moves until you say so. That is the point: you
   fired one request, you know its path, and you know what you broke.
4. **Start from an example.** Seventeen prebaked scenarios, grouped.
5. **Share the link.** The diagram travels in the URL, so a link is a runnable
   repro.

The diagram lives in the URL **fragment**, not the query string. That is a privacy
decision rather than tidiness: the pitch is "paste a diagram of your system", so
real internal service names land in these links, and a query string reaches server
logs, CDN logs and `Referer` headers. A fragment never leaves the browser.

## The diagram language

Plain Mermaid, read for meaning. The shapes already map onto OpenTelemetry
semantic conventions, so nothing new has to be learned.

| You write | Shoebox reads |
|---|---|
| `api[Orders API]` | a service |
| `db[(Postgres)]` | a datastore |
| `q[[Job Queue]]` | a queue |
| `cache((Redis))` | a cache |
| `ext{{Stripe}}` | a third party |
| `worker[Worker x5]` | five replicas, load balanced |
| `worker[Worker #2]` | one named instance |
| `a -->\|broken\| b` | this call always fails |
| `a -->\|broken: wrong table\| b` | and this is why |
| `a -->\|broken on #3\| b` | only instance 3 fails |
| `q -->\|phantom\| b` | b never runs, so nothing consumes what q published |

**A phantom is a dead consumer**, the same thing
[Snowglobe](https://github.com/ImmersiveFusion/snowglobe) means by it: *services you
did not know you had, so the platform infers the missing ones from the topology*.

It is not a failure. `-->|broken|` leaves a span with an error on it, which is
evidence. `-->|phantom|` leaves nothing: no span from that service, no span naming
it, no entry in served-by, and nothing downstream of it either. Every span in the
trace is green.

What makes it findable at all is the half that still happens. The producer
publishes normally and its span carries `messaging.destination.name`, and no
receive ever correlates to it. A backend can tell something ought to be consuming
that destination; the trace can only show you that nothing did. Put the consumer
back and the phantom disappears, which is the point.

**Which means a queue needs a far side.** Draw `orders --> q[[Job Queue]]` and stop
there, and the run publishes to a destination nothing receives: the same telemetry
a declared phantom produces, span for span, so a backend reports it unconsumed
without anyone having written the word. A datastore may be the last thing in a
diagram, because a trace only ever learns about one from its caller. A queue may
not, because the whole reason to draw a queue is what happens on the other side.
The run tells you when you have done it.

**The broker comes off the label.** `q[[Kafka]]` publishes with
`messaging.system = kafka`, and a label naming no broker gets `rabbitmq`. That
matters more than it looks: a reader keys a queue on (system, destination) and
often *labels* the node with the system, so a hardcoded one puts every queue in
your diagram on screen under the same name.

**Replicas are load balanced. Separate arrows are fan-out.** `q --> worker[Worker x5]`
sends one request to *one* worker. Two arrows out of one node call *both*.

**Labels become telemetry names, and are slugified on the way.** A queue label goes
through the same slugifier as a service name, so `q[[orders.created]]` publishes to a
destination called `orders-created`. Worth knowing before you paste a real destination in
and go looking for it in your backend.

Each simulated pod gets its own `TracerProvider` with a Resource carrying
`service.name`, and its own `ActivitySource`. That is the same pattern
[Snowglobe](https://github.com/ImmersiveFusion/snowglobe) runs at 28 services and
59 pods, so the shape of the output is proven rather than invented here.

## Where the telemetry goes

One destination per instance, decided at startup by whoever runs it. Snowglobe
takes `-endpoint` and `-headers` on the command line; Shoebox reads the same two
things from configuration, in the same two formats, so knowing one tool means
knowing the other.

| Setting | Environment equivalent | Effect |
|---|---|---|
| `Otlp:Endpoint` | `Otlp__Endpoint` | Where to export. A URL, or a bare `host:port`, which is assumed to be TLS |
| `Otlp:Headers` | `Otlp__Headers` | `key=value` pairs, comma-separated |

When those are empty the standard OpenTelemetry variables are read instead, so an
environment already configured for OTel needs nothing Shoebox-specific:
`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, then `OTEL_EXPORTER_OTLP_ENDPOINT`, and
`OTEL_EXPORTER_OTLP_HEADERS`.

Jaeger, Tempo, Grafana, SigNoz, a Collector, or anything else that speaks OTLP. No
vendor name appears anywhere in the decision, which is the point.

**Nothing configured is a supported state.** Runs still execute, they just do not
go anywhere, and the UI says so, because a person seeing no traces needs to tell an
unset endpoint from a broken diagram. What is *not* supported is a broken endpoint:
a malformed `Otlp:Endpoint` refuses to start rather than exporting nowhere quietly
for the life of the deployment.

**A visitor has no say in this and needs none.** Nothing about a run carries a
destination, so a request cannot redirect telemetry. Whoever is looking at a
deployed instance reads their traces in whatever backend that deployment is wired
to.

### Configuring a deployment

Application settings on the host, the ordinary way. On Azure App Service, an app
setting named `Otlp__Endpoint` becomes `Otlp:Endpoint` with no code involved. Do
not put a key in `appsettings.json`: it is committed.

### Configuring your machine

Every settings file in this repo is committed, `appsettings.Development.json` and
`launchSettings.json` included, so an API key in any of them is an API key in the
history. Local settings go in user secrets instead, which live under your profile
rather than the working tree and are read in Development only:

```bash
dotnet user-secrets --project src/Shoebox.Api set "Otlp:Endpoint" "otlp.example.com:443"
dotnet user-secrets --project src/Shoebox.Api set "Otlp:Headers"  "api-key=YOUR_KEY"
```

`dotnet user-secrets --project src/Shoebox.Api list` shows what is set, and prints
the key in full, so mind who is looking.

Environment variables work as well and need no project change, which suits a
one-off run:

```bash
Otlp__Endpoint=otlp.example.com:443 Otlp__Headers="api-key=YOUR_KEY" \
  dotnet run --project src/Shoebox.Api
```

That does put the key in shell history and in the process listing, so it is the
worse of the two for anything you type twice.

Confirm either one landed without going near the UI:

```bash
curl -s localhost:5168/otlp/status
```

`configured: true` and the endpoint echoed back means the exporter is on. It does
not mean the backend accepted anything: export is best effort and a run will not
fail because the far end refused it, so check your backend for the trace id the run
returns.

## For agents

Models write Mermaid fluently and reach for me unprompted, so the instructions they
need are served rather than assumed:

**https://shoebox.deepcube.ai/llms.txt**

It covers minting a shoebox, the diagram language, every arrow form, firing, reading a
run back, the share-link format, and the pacing below.

Writing it found a real hole. A model writes far more Mermaid than the parser used to
read, and everything outside the subset became a note **while the run still returned
200 with a trace id** — a green trace for a system nobody drew, which is worse than a
parse failure because a parse failure is visible. A real Azure reference architecture,
pasted unedited, came through as 6 of its 11 edges with 18 ignored lines and its
service names carrying the `n` from a `
` escape.

Chains (`a --> b --> c`), dotted arrows (`-.->`, `-. text .->`), thick arrows (`==>`),
inline labels (`-- text -->`) and undirected links (`---`) are all read now, and the
same diagram comes through as 12 of 12. `subgraph`/`end`/`direction`/`style` are
skipped in silence, because they are understood perfectly well and simply have no
counterpart in a trace. Two judgements are reported rather than assumed: an undirected
link is read left to right *and says so in the notes*, and an edge pointing at a
subgraph becomes a service *and says so*. A line is all or nothing — half a parsed
line is how a diagram silently becomes a different one.

`POST /topology/parse` is free, emits nothing, and is still the thing to call before
firing anything you did not write yourself.

### Pacing

One shared instance serves everybody, so runs are throttled. Enforced in the app, and
restated in every 429 alongside a `Retry-After`:

| What | Limit |
|---|---|
| `POST /run` per shoebox | 5 in a row, then one every 5 minutes |
| `POST /run` per source address | twice that, across every shoebox it mints |
| `POST /shoebox` per source address | 5, then one every 5 minutes |
| `POST /topology/parse` per source address | 60 a minute |

The burst is deliberate: replica selection is round robin on `runIndex`, so seeing what
`broken on #3` of five does takes five runs and should not be a twenty-five minute
errand. The per-source layer exists because a shoebox id is minted by asking for one,
so a limit keyed on it alone is evaded by asking again.

This is politeness enforcement, not abuse defence. A distributed slam is a job for the
edge; this is what stops the ordinary way a public tool falls over, which is an agent
in a loop.

## Running it

### Prerequisites

- **.NET 10 SDK**
- **Node.js >= 24.15.0.** Not optional and not a warning: the Angular 22 CLI
  refuses to start on anything older, and it says so in a message that looks
  nothing like a build error. `npm ci` only warns, which makes this easy to
  misdiagnose as an Angular problem.

Nothing else. No SQL Server, no Redis, no containers.

### Two processes

```bash
# API on 5168. Where it exports comes from user secrets, see above. With none
# set, runs still execute, they just do not go anywhere, and the UI says so.
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5168 \
  dotnet run --project src/Shoebox.Api --no-launch-profile

# SPA on 4200, proxied to the API by proxy.conf.json
npm --prefix src/Shoebox.Spa ci
npm --prefix src/Shoebox.Spa start
```

Open `http://localhost:4200`. The proxy is what makes the second process useful:
the SPA calls relative paths, so without it every request lands on the dev server
and nothing fires.

### Checking the API on its own

The fastest way to tell an API problem from a UI one:

```bash
curl -X POST http://localhost:5168/shoebox

curl -X POST "http://localhost:5168/run?shoeboxId=$ID" \
  -H "Content-Type: application/json" \
  -d '{"diagram":"flowchart LR\n  api[Orders API] -->|broken: wrong table| db[(SQL Server)]","runIndex":1}'
```

### Building and testing

```bash
npm --prefix src/Shoebox.Spa run build -- --configuration production   # ~434 kB initial, no budget warning
dotnet test tests/Shoebox.Api.UnitTests/Shoebox.Api.UnitTests.csproj    # 65 tests
```

## How isolation works

One shared instance serves everyone. Isolation is logical, not provisioned: each
visitor gets a GUID `shoeboxId`, it rides OpenTelemetry Baggage onto every span as
`shoebox.id`, and per-shoebox state is held in memory and deliberately not
persisted.

That is a live demonstration of baggage propagation inside a tool for learning to
read telemetry, and it is also what makes a no-signup, no-install public tool
sustainable.

## For contributors

Six things in here will cost you an afternoon if nobody tells you.

**Never apply `transition-duration` to `*`.** A `prefers-reduced-motion` block
doing that reaches inside the rendered SVG, and Mermaid then lays the graph out
roughly forty times too large: nodes at the correct size but two thousand pixels
apart, so the whole diagram scales to hairlines and the pane looks blank. The rule
in `styles.scss` is scoped `*:not(svg):not(svg *)` for exactly this reason. Do not
simplify it back. Bisected: `animation-duration` and `animation-iteration-count`
are harmless, `transition-duration` alone does it.

**Decoration must never cost a render.** `decorate()` in `diagram-style.ts`
appends `classDef`, `class` and `linkStyle` to the source and is wrapped in a
try/catch that returns the original text. A styling bug must not present to the
user as a syntax error. It also bails out entirely if the diagram already contains
`classDef`, `style` or `linkStyle`, on the grounds that whoever wrote those has
opinions.

**Color carries meaning.** Teal is ours, violet is a third party (the one box you
cannot go and fix), magenta is only ever damage. Spend magenta on decoration and
the broken edge stops being the loudest thing on screen.

**Mermaid is lazy-loaded on purpose.** It is roughly a quarter of a megabyte and
blows the 500 kB budget when bundled. The distribution model is somebody clicking
a link in a forum thread, so the page paints first and the renderer arrives after.

**A run must not inherit ASP.NET's `Activity`.** ASP.NET leaves an unsampled `Activity`
on the request, and a parent-based sampler then drops every simulated span beneath it:
runs come back with zero spans while the hops and the notes look perfectly fine, which
reads like an exporter problem and is not one. `TopologyRunner` detaches from
`Activity.Current` before a run and restores it after. Do not simplify that away.

**A running instance locks the build output.** Any `Shoebox.Api` you left running holds
`src/Shoebox.Api/bin/`, and `dotnet build` or `dotnet test` then fails with MSB3027 rather
than with anything about your change. `dotnet msbuild -t:Compile` type-checks without
touching `bin/`; for anything that has to actually run, copy the project to a scratch
directory outside the tree.

Which leads to the rule worth more than the other six: **if the claim is about what the
telemetry contains, print the telemetry.** A session once went seven rounds reading code
for a defect that one pass of printing every emitted `service.name` found immediately.
`tests/Shoebox.Api.UnitTests/Run/QueueShapeDumpTests.cs` is where that lives now.
Extend it before reaching for a throwaway harness.

Adding an example must never require code. `examples.ts` is pure data. If a new
scenario needs a code change, the design has gone wrong.

## Status

**Live at [shoebox.deepcube.ai](https://shoebox.deepcube.ai).** Paste a diagram,
break something, fire one request, read what comes out. No account and no install,
which is the whole promise, and it is kept.

[chaos.deepcube.ai](https://chaos.deepcube.ai/) points at the same deployment, so a
link somebody saved before the rework still lands on something current rather than
on the old build that needed SQL Server and Redis.

Deploys run from a release pipeline outside this repository, which is why there is
no Dockerfile and no workflow in here to find. What is genuinely missing is
continuous integration: the tests run when somebody runs them, not on every pull
request, so a green tree here is a claim rather than something the repository
checked.

## The family

| | What it is |
|---|---|
| **[Snowglobe](https://github.com/ImmersiveFusion/snowglobe)** | The sealed world, pre-made. One binary generating a 28-service system emitting logs, traces and metrics, with failures injected on purpose. You shake it. |
| **Shoebox** | The world you build. Start from a template, change it, fire one request, look. You reach in and rearrange it. |
| **[sos-beacon](https://github.com/ImmersiveFusion/sos-beacon)** | Listens to the **real** world. Not a miniature. |

A snowglobe is sealed. In a shoebox you can open it up. Same OTLP, opposite
directions.

## Contributing

Contributions welcome. [Open an issue](https://github.com/ImmersiveFusion/shoebox/issues)
or send a PR:

- More example scenarios, which are pure data
- Mermaid syntax the parser does not read yet
- Anything that makes the output more useful to read

## Connect

[Email](mailto:info@immersivefusion.com) |
[LinkedIn](https://www.linkedin.com/company/immersivefusion) |
[Discord](https://discord.gg/zevywnQp6K) |
[GitHub](https://github.com/immersivefusion) |
[YouTube](https://www.youtube.com/@immersivefusion)

## License

Apache License 2.0, see [LICENSE](LICENSE).

Copyright 2026 [ImmersiveFusion](https://immersivefusion.com)
