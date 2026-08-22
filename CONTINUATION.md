# Continuation note

Handoff for a new session. Written 2026-08-22.

## Branches in play

| Repo | Path | Branch |
|------|------|--------|
| Shoebox | `m:\dobri\IF\repos\shoebox` | `feat/synthetic-mermaid` |
| IF.Knowledge.Marketing | `m:\dobri\IF\repos\IF.Knowledge.Marketing` | `trout/snowglobe-rename-and-distribution-playbook` |

Both are clean and pushed. The marketing branch was untouched this session; the
work below is all in Shoebox. The last marketing commit is `0dfc5c8`
(MKT-SP-121, worker permutation prebaked).

## Where Shoebox got to

Last three commits on `feat/synthetic-mermaid`:

- `c8ef44b` Rework the UI into the Mermaid paste box
- `6fc6263` Put the brand back, and take 145 kB of dead CSS out
- `9bb8a9e` Style the diagram by role, and fix the layout bug I introduced

The SPA is now a paste box: a textarea, a live mermaid render on a 300 ms
debounce, a grouped example picker (sixteen prebaked scenarios, pure data in
`examples.ts`), and one fire button with a run counter and a served-by instance
list. Rendering is branded per role in `diagram-style.ts`.

## Things that will bite you if you do not know them

**A blanket `transition-duration` breaks mermaid layout.** A
`prefers-reduced-motion` block applying `transition-duration` to `*` reaches
inside the rendered SVG, and mermaid then lays the graph out roughly forty times
too large -- nodes at the correct size but two thousand pixels apart, so the
whole diagram scales to hairlines and the pane looks blank. The rule in
`styles.scss` is scoped `*:not(svg):not(svg *)` for exactly this reason. Do not
"simplify" it back. Bisected: `animation-duration` and
`animation-iteration-count` are harmless, `transition-duration` alone does it.

**Decoration must never cost a render.** `decorate()` in `diagram-style.ts`
appends `classDef`/`class`/`linkStyle` to the source and is wrapped in a
try/catch that returns the original text. A styling bug must not present to the
user as a syntax error. It also bails out entirely if the diagram already
contains `classDef`, `style` or `linkStyle`, on the grounds that whoever wrote
those has opinions.

**The diagram lives in the URL hash, not the query string.** Deflate plus
base64url, in `diagram-url.ts`. This is a privacy decision, not tidiness: the
pitch is "paste a diagram of your system", so real internal service names land in
these links, and a query string reaches server logs, CDN logs and `Referer`
headers. A fragment never leaves the browser. It costs nothing because running is
a POST. The user was told and did not object, but it does differ from the
original ask, which was a query string.

**Mermaid is lazy-loaded on purpose.** It is roughly a quarter megabyte and blew
the 500 kB budget when bundled. Do not move it back into the initial bundle to
"simplify" the component. Distribution model is somebody clicking a link in a
forum thread.

**Colour has meaning here.** Teal is ours, violet is a third party (the one box
you cannot go and fix), magenta is only ever damage. Do not spend magenta on
decoration or the broken edge stops being the loudest thing on screen.

## Verify like this

```
cd src/Shoebox.Spa && npx ng build --configuration production   # expect ~432 kB, no budget warning
dotnet test tests/Shoebox.Api.UnitTests/Shoebox.Api.UnitTests.csproj   # 25 tests
```

To actually look at it rather than trusting the compiler -- which is how both real
bugs this session were found:

```
cd src/Shoebox.Spa/dist/shoebox.spa/browser && python -m http.server 8231
"/c/Program Files/Google/Chrome/Application/chrome.exe" --headless=new --disable-gpu \
  --hide-scrollbars --virtual-time-budget=10000 --window-size=1440,1150 \
  --screenshot=shot.png http://127.0.0.1:8231/
```

Note for probing mermaid directly from `node_modules`: Python's `http.server`
does not map `.mjs` to a JavaScript MIME type, so the module silently fails to
import. Map it or the probe will look like a mermaid bug.

## Open, not done

- **The footer reads "Your sandbox is , and it rides..." with an empty id** when
  the API is not running, because `createSandbox` never returns and `sandboxId`
  stays `''`. Cosmetic, pre-existing, visible in every screenshot. Needs a guard.
- **`README.md` still describes the old chaos-simulator behaviour**, not the paste
  box. It is stale.
- **Nothing has been clicked in a real browser.** Everything is verified by build,
  unit test, and headless screenshot. Fire-one-request has never been exercised
  against a running API in this session, so the run counter, the served-by list
  and the OTLP status line are unproven end to end.
- The old `.img/banner.jpg` predates the rename and may still say the old name.
- **Pre-rename names still in the tree:** `README.md`, `src/Shoebox.Spa/README.md`
  and `src/Shoebox.Api/Shoebox.Api.http` still carry `ExampleSpa`/`Example.Api`
  strings. Cosmetic, but they are user-facing in a repo being handed to strangers.

## The rename, and where it actually stands

Two repos were renamed on GitHub 2026-08-20. Names are **locked by the founder**
and are not conditional on anything.

| Was | Is |
|---|---|
| `ImmersiveFusion/opentelemetry-tracegen` | `ImmersiveFusion/snowglobe` |
| `ImmersiveFusion/opentelemetry-chaos-sim` | `ImmersiveFusion/shoebox` |

**Snowglobe is done.** v0.9.1 released, `go install` verified, image published as
`immersivefusion/snowglobe`, README and description live. The old
`immersivefusion/tracegen` Docker repo was archived by the founder, so the
dual-publish alias was removed. Two non-blocking leftovers: the `genai` topic is
not added, and the GitHub social preview 404s, which is a GitHub-side fault.

**Shoebox is the active lane**, and it is what this session worked on.

**Deliberately NOT renamed, because they are identifiers and renaming them breaks
users:** the `tracegen.` metric namespace emitted by `metrics.go`, and
`TRACEGEN_LOG_LEVEL`. Each needs its own deprecation cycle. Do not "finish the
rename" by sweeping these.

**The sibling framing, which the copy depends on:** Snowglobe is a sealed world
you shake; a shoebox is one you can open up. `sos-beacon` is the third sibling,
emitting organic telemetry where Snowglobe synthesizes it. The footer and the
masthead in this app both lean on that, so do not reword them casually.

### Canon drifts from the artifact here. Check before you trust.

The spike records **nine** occasions where the written canon disagreed with the
live repo, three of them inside its own handoff. Verified this session, and the
spike's "next actions" list is now stale on two counts:

| Handoff says | Actually |
|---|---|
| Item 4: bump Node to >= 24.15.0 | **Done.** v24.19.0 |
| Item 5: .NET 9 to 10, Angular 21 to 22, TS 5.9 to 6.0, then CPM | **Done.** net10.0, Angular 22.1.3, CLI 22.1.5, TS 6.0.3, `Directory.Packages.props` with 18 `PackageVersion` entries |
| "one csproj" | **Two.** `Shoebox.Api` plus `Shoebox.Api.UnitTests` |

Read the repo before believing any note, including this one.

---

## Deployment: what exists, and what does not

**There is no deployment pipeline in this repo. At all.** Verified this session:
no `Dockerfile`, no `.github/workflows`, no bicep, no terraform. Whatever puts the
current sandbox on the internet lives outside this repo, and standing the reworked
app up is unbuilt work, not a config tweak.

**Use the `az` CLI, not the Azure MCP server.** Founder instruction this session.

### The surfaces

| Surface | State |
|---|---|
| `chaos.deepcube.ai` | Live. **Hard constraint: stays up throughout.** |
| `demo.iapm.app` | Named in `rename-spec.md` as where the sandbox is deployed, serving the "ExampleSpa" title |
| `snowglobe.run` | **The target. Not confirmed bought** (founder action). Apex versus subdomain undecided. Everything downstream assumes it. |
| `demo.deepcube.ai` | Does not resolve |

The first two are named in different spike documents and may or may not be the
same deployment. **Reconcile that against reality before planning a cutover**, and
none of the four was verified live this session.

### What the rework changes about deploying

**One shared instance serves everyone.** Isolation is logical, not provisioned:
each visitor gets a GUID `sandboxId` on the query string, propagated through
OpenTelemetry Baggage and tagged on every span, with per-sandbox state held in
memory and deliberately not persisted. This is the design choice that makes a
no-signup, no-install, no-cost public tool sustainable, and **it must survive
unchanged in principle.**

Going fully synthetic removes SQL Server and Redis, so the deployment is now just
the .NET API plus a static Angular bundle, and isolation is close to free. An OTLP
endpoint still needs configuring; the UI surfaces whether one is set, which is why
that status line exists.

### Three defects the rename was meant to fix

1. **The "ExampleSpa" page title.** **Fixed this session** -- the title is now
   "Shoebox: a small system you can break".
2. **The pre-rename URL**, still open and blocked on the domain.
3. **No canonical home for the OSS family**, still open, same blocker.

### The cutover rule, which is not a checklist

Nothing is cut over until **the person who gives the demo for real** runs it on
the new build and says it is as good or better. The canonical demo is *"this is
what happens when a broken SQL runs."* The 4-service pipeline ships as a stock
template and break-the-SQL stays a one-click interaction. This is a migration, not
a greenfield build, and treating it as greenfield is how it goes wrong.

---

## Sync

**Governance sync is stale.** `.context/upstream/sync-status.md` in the marketing
repo last synced **2026-06-26**, roughly two months before this note. That covers
the baseline, rules, quality SSOT and specialist registry pulled from IF.Knowledge.

**Friday sequences the cross-property sweep.** Nothing outside the Trout lane gets
swept until Friday orders it. The Docker alias is no longer part of that wait.

**Unshipped and still the highest-value item:** both repo descriptions and topics,
paste-ready in `repo-copy.md`. They still describe tools that no longer exist by
those names, and descriptions are what GitHub search indexes and what every link
preview renders. Gating item for the OTel registry and awesome-opentelemetry
submissions, which are otherwise unblocked.

**Manual, cannot be automated:** paste `DOCKERHUB.md` into the Docker Hub repo
overview by hand. The description API rejects PATs.

**Content flows one way** in the marketing repo: `.context/current/compare/*.md`
is canonical, the storefront renders it down. Never the reverse.

## Environment

This machine ran out of both RAM and disk during the session: three failures,
including a production build killed at 0 bytes free on `C:` of 244 GB. If a build
or a git credential helper dies for no reason, check free space and memory before
debugging the code.
