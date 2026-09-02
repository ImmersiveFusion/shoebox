# Contributing to Shoebox

Thanks for looking. Shoebox is a small system you can break: paste a Mermaid diagram,
break a call, fire one request, get real OpenTelemetry out. Bug reports, scenarios,
code and docs are all welcome.

## The one thing to keep in mind

**Shoebox is fully synthetic and stays that way.** There is no database, no cache, and
no real infrastructure behind it. The diagram is the whole state. That is what makes it
safe to hand to a stranger and cheap to run, and it is the property most worth
protecting.

So a change that introduces real persistence, real downstream services, or per-user
server-side state is going the wrong way, however useful it looks. If you want
something that needs one of those, open an issue first and let us talk about it before
you write anything.

## Building it

You need the **.NET 10 SDK**. For the front end you also need **Node 22 or newer**.

```bash
git clone https://github.com/ImmersiveFusion/shoebox.git
cd shoebox

# API
dotnet restore src/Shoebox.sln
dotnet build   src/Shoebox.sln
dotnet run --project src/Shoebox.Api

# SPA, in a second terminal
cd src/Shoebox.Spa
npm ci
npm start
```

## Running the tests

```bash
dotnet test src/Shoebox.sln
```

The suite lives in `tests/Shoebox.Api.UnitTests` and covers the Mermaid parser, the
topology runner, the queue shapes, share links and the OTLP emit path. **If you change
behaviour in `src/Shoebox.Api`, add or update a test.** CI runs the same command on
every pull request, so a red build is the first thing a reviewer will see.

## Sending telemetry somewhere real, locally

The OTLP endpoint and its API key are deliberately **not** in the repository.
`appsettings.json`, `appsettings.Development.json` and `launchSettings.json` are all
tracked, so a key in any of them is a key in the git history forever. Use user secrets
instead:

```bash
dotnet user-secrets --project src/Shoebox.Api set "Otlp:Endpoint" "otlp.example.com:443"
dotnet user-secrets --project src/Shoebox.Api set "Otlp:Headers"  "api-key=YOUR_KEY"
```

**Never put a real endpoint or key in a commit, a test fixture, or an issue.** If you
think you have committed one, say so immediately rather than quietly force-pushing;
see [SECURITY.md](https://github.com/ImmersiveFusion/.github/blob/main/SECURITY.md).

## Where things live

| Path | What it is |
|---|---|
| `src/Shoebox.Api/Topology/` | Mermaid parsing and the topology model |
| `src/Shoebox.Api/Run/` | The runner that walks a topology for one request |
| `src/Shoebox.Api/Emit/` | OTLP export and the tracer pool |
| `src/Shoebox.Api/Share/` | Share links, which encode the diagram into the URL |
| `src/Shoebox.Api/Session/` | Per-session middleware and telemetry wiring |
| `src/Shoebox.Spa/` | The Angular front end |
| `tests/Shoebox.Api.UnitTests/` | The test suite |

## Good places to start

- **Add a scenario.** The prebaked examples are the fastest way for a newcomer to see
  the whole pipeline, and a good scenario is a genuine contribution rather than a
  chore.
- **Improve the Mermaid parser's tolerance.** Real diagrams people paste are messier
  than the ones we wrote.
- **Anything labelled `good first issue`.**

## Pull requests

- Branch from `main`.
- Keep the change focused. One idea per pull request reviews faster than three.
- Say how you tested it. "Added a test" or "ran it against a local collector and
  checked the spans" both work; "did not test" is an acceptable answer, just say it.
- CI must be green.

## Licence

Shoebox is Apache-2.0. By contributing you agree that your contribution is licensed
under the same terms.
