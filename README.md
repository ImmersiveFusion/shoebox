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
4. **Start from an example.** Sixteen prebaked scenarios, grouped.
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

**Replicas are load balanced. Separate arrows are fan-out.** `q --> worker[Worker x5]`
sends one request to *one* worker. Two arrows out of one node call *both*.

Each simulated pod gets its own `TracerProvider` with a Resource carrying
`service.name`, and its own `ActivitySource`. That is the same pattern
[Snowglobe](https://github.com/ImmersiveFusion/snowglobe) runs at 28 services and
59 pods, so the shape of the output is proven rather than invented here.

## Where the telemetry goes

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to any OTLP backend: Jaeger, Tempo, Grafana,
SigNoz, a Collector, or anything else that speaks OTLP. The UI tells you whether
one is configured, so a user who sees no traces can tell an unset endpoint from a
broken diagram.

Runs still execute with nothing configured. They just do not go anywhere.

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
# API on 5168
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
curl -X POST http://localhost:5168/sandbox

curl -X POST "http://localhost:5168/run?sandboxId=$ID" \
  -H "Content-Type: application/json" \
  -d '{"diagram":"flowchart LR\n  api[Orders API] -->|broken: wrong table| db[(SQL Server)]","runIndex":1}'
```

### Building and testing

```bash
npm --prefix src/Shoebox.Spa run build -- --configuration production   # ~428 kB initial, no budget warning
dotnet test tests/Shoebox.Api.UnitTests/Shoebox.Api.UnitTests.csproj    # 25 tests
```

## How isolation works

One shared instance serves everyone. Isolation is logical, not provisioned: each
visitor gets a GUID `sandboxId`, it rides OpenTelemetry Baggage onto every span as
`sandbox.id`, and per-sandbox state is held in memory and deliberately not
persisted.

That is a live demonstration of baggage propagation inside a tool for learning to
read telemetry, and it is also what makes a no-signup, no-install public tool
sustainable.

## For contributors

Four things in here will cost you an afternoon if nobody tells you.

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

Adding an example must never require code. `examples.ts` is pure data. If a new
scenario needs a code change, the design has gone wrong.

## Status

The rework described above is **not deployed anywhere yet**. There is no
Dockerfile, no workflow and no infrastructure-as-code in this repo, and nothing
copies the built front end into the API's `wwwroot`. Running it locally is the
only way to use it today.

[chaos.deepcube.ai](https://chaos.deepcube.ai/) is still serving the previous
version of this project, the one that needed SQL Server and Redis. It stays up
until whoever gives the demo for real runs it on the new build and says it is as
good or better.

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
