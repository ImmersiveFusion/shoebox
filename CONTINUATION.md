# Continuation note

Handoff for a new session. Written 2026-08-22, updated 2026-08-23 after
running the app for real.

## Branches in play

| Repo | Path | Branch |
|------|------|--------|
| Shoebox | `m:\dobri\IF\repos\shoebox` | `feat/synthetic-mermaid` |
| IF.Knowledge.Marketing | `m:\dobri\IF\repos\IF.Knowledge.Marketing` | `trout/snowglobe-rename-and-distribution-playbook` |

The marketing branch is untouched. The last marketing commit is `0dfc5c8`
(MKT-SP-121, worker permutation prebaked).

**The local clone paths above are correct only since 2026-08-23.** Before that,
this repo sat at `m:\dobri\IF\repos\opentelemetry-chaos-sim` with its remote
still pointing at the pre-rename URL, and the sibling at
`m:\dobri\IF\repos\opentelemetry-tracegen`. Both directories and both remotes
were renamed on 2026-08-23. If a note tells you to look in
`m:\dobri\IF\repos\shoebox` and it is not there, look under the old name
before assuming the work is lost: everything was on `origin` the whole time.

## Where Shoebox got to

Commits on `feat/synthetic-mermaid`:

- `c8ef44b` Rework the UI into the Mermaid paste box
- `6fc6263` Put the brand back, and take 145 kB of dead CSS out
- `9bb8a9e` Style the diagram by role, and fix the layout bug I introduced
- `52b91e6` Prove it in a browser, and fix what that turned up

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

Both were re-run on 2026-08-23. The build lands at 428 kB initial and the tests
are 25 of 25.

**Check `node -v` first.** See the Node row in the drift table below: the Angular
CLI refuses to start on anything below v24.15.0, and it says so in a message that
looks nothing like a build error.

To run the whole thing rather than half of it, two processes:

```
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5168 \
  dotnet run --project src/Shoebox.Api --no-launch-profile
npm --prefix src/Shoebox.Spa start        # 4200, proxied to 5168
```

`proxy.conf.json` is what makes the second one useful and it is new as of
2026-08-23. Without it the SPA posts to its own origin and nothing fires.

The API alone can be checked with curl, which is the fastest way to tell an API
problem from a UI one:

```
curl -X POST http://localhost:5168/sandbox
curl -X POST "http://localhost:5168/run?sandboxId=$ID" -H 'Content-Type: application/json' \
  -d '{"diagram":"flowchart LR\n  api[Orders API] -->|broken: wrong table| db[(SQL Server)]","runIndex":1}'
```

To drive the page rather than only photograph it, launch Chrome with
`--headless=new --remote-debugging-port=9222 --user-data-dir=<scratch>` and talk
to it over the DevTools protocol. Node 24 has a global `WebSocket`, so a forty
line script is enough: read `http://127.0.0.1:9222/json/list` for the page
target, `Runtime.evaluate` to click and to read text back, `Page.captureScreenshot`
for the picture. That is how the run counter and the served-by list were finally
verified. Two traps in writing one: a real newline inside a regex literal in an
evaluated expression fails as "Invalid regular expression: missing /", and
`--user-data-dir` must be outside the repo or Chrome leaves a profile behind in
the tree.

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

## Closed 2026-08-23

- **It has been clicked in a real browser.** API and dev server run together,
  Chrome driven over the DevTools protocol, fire button clicked twice. The run
  counter incremented 3 to 4 to 5, the served-by list came back live, and the
  OTLP status line rendered the unconfigured branch correctly. **The per-instance
  break works end to end:** on the `broken on #3` example the run that landed on
  `worker-3` failed and the run that landed on `worker-4` did not, which is the
  whole point of the feature and had never been observed.
- **The empty sandbox id in the footer** is guarded. The sentence is hidden until
  there is an id, rather than rendering `Your sandbox is , and it rides ...`.
- **`Shoebox.Api.http`** covers the four real endpoints instead of `Example.Api`
  and a `weatherforecast` route that has not existed for some time.
- **`src/Shoebox.Spa/README.md`** is a real README instead of the Angular CLI
  default opening `# ExampleSpa`.
- **The two `environment.ts` files are deleted.** Nothing imported them and they
  carried a pre-rename GitHub URL plus hardcoded internal Azure hostnames into a
  repo being handed to strangers.

## Open, not done

- **The root `README.md` still describes the old chaos-simulator behaviour**, not
  the paste box. Still stale, and it is the first thing a stranger reads.
- **The diagram renders small in a tall empty pane.** A six-node LR flow comes out
  538 by 81 pixels inside a viewer roughly 500 pixels tall, so the labels are
  around six pixels and most of the panel is background. A design call rather than
  a bug, but it is the first thing you notice looking at the page.
- **Nothing copies the SPA build into `src/Shoebox.Api/wwwroot`.** `AddSpaStaticFiles`
  points at `wwwroot` and `wwwroot` is empty, so outside Development the API serves
  no front end at all. Another face of "there is no deployment pipeline in this repo".
- The old `.img/banner.jpg` predates the rename and may still say the old name.
- **338 MB of dead build output is still on disk:** `src/Example.Spa` (309 MB, only
  `dist`, `node_modules` and empty `.angular` and `.claude` directories) and
  `src/Example.Api` (29 MB, only `bin`, `obj` and a `.csproj.user`), plus
  `src/.vs/Example` and `src/Example.sln.DotSettings.user`. All untracked leftovers
  of projects that no longer exist in the tree. Removing them was refused by a
  permission prompt on 2026-08-23, so it is still to do, and this machine has run
  out of disk before.
- **The `snowglobe` clone is four commits behind `origin/main`**, so its working
  tree still has `WHERE-TRACEGEN-RUNS.md` and a stale `tracegen.exe`. The
  fast-forward was refused by the same prompt. Nothing is lost; it is one
  `git merge --ff-only origin/main` away.

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
live repo, three of them inside its own handoff. Call it **eleven** now: the
2026-08-23 session found that this note's own Node claim was false on the machine
it was read on, and that the clone paths it gave did not exist because the
directories had never been renamed locally. Both were found the same way, by
looking rather than by trusting.

Verified against the live repo, here is where the spike's "next actions" list
stands:

| Handoff says | Actually |
|---|---|
| Item 4: bump Node to >= 24.15.0 | **Not on this machine.** The only Node on PATH is v24.11.1 and `@angular/cli` 22 refuses to start on it. The 2026-08-22 claim of v24.19.0 was true of wherever that session ran, not here. Worked around on 2026-08-23 with a portable Node 24.19.0 unpacked into a scratch directory and put in front of PATH, so the blocker returns the moment you open a fresh shell. |
| Item 5: .NET 9 to 10, Angular 21 to 22, TS 5.9 to 6.0, then CPM | **Done.** net10.0, Angular 22.1.3, CLI 22.1.5, TS 6.0.3, `Directory.Packages.props` with 18 `PackageVersion` entries |
| "one csproj" | **Two.** `Shoebox.Api` plus `Shoebox.Api.UnitTests` |

Read the repo before believing any note, including this one. Two of the three
rows above were wrong the last time somebody checked, and the note doing the
correcting was this file.

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
