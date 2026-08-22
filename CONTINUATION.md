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

## Environment

This machine ran out of both RAM and disk during the session: three failures,
including a production build killed at 0 bytes free on `C:` of 244 GB. If a build
or a git credential helper dies for no reason, check free space and memory before
debugging the code.
