# Shoebox.Spa

The Shoebox front end: a paste box for Mermaid diagrams that renders the system
you described, lets you break a call, and fires one request through it.

Angular 22, TypeScript 6, built with the Angular CLI. It is served as static
files by `Shoebox.Api`, which also owns every endpoint it calls.

## Run it

```
npm ci
npm start          # dev server on http://localhost:4200, proxied to the API
```

The API must be running for fire-one-request to do anything:

```
dotnet run --project ../Shoebox.Api
```

## Build

```
npm run build -- --configuration production
```

Output lands in `dist/shoebox.spa/browser`. The production budget is deliberate:
mermaid is roughly a quarter of a megabyte and is lazy-loaded rather than bundled,
because the distribution model is somebody clicking a link in a forum thread.

## Test

```
npm test           # Karma and Jasmine
```

## What lives where

| File | Responsibility |
|---|---|
| `src/app/shoebox/shoebox.component.ts` | The whole interaction: paste, render, break, fire |
| `src/app/shoebox/examples.ts` | The prebaked scenarios, pure data. Adding one must never require code |
| `src/app/shoebox/diagram-style.ts` | Per-role styling, appended to the source. Must never cost a render |
| `src/app/shoebox/diagram-url.ts` | Diagram in the URL hash, deflate plus base64url |
| `src/app/shoebox/shoebox.service.ts` | The API calls |

## Two things that will bite you

**Do not apply `transition-duration` to `*`.** It reaches inside the rendered SVG
and mermaid then lays the graph out roughly forty times too large, which presents
as a blank pane rather than as a styling bug. The rule in `styles.scss` is scoped
`*:not(svg):not(svg *)` for exactly that reason.

**Colour carries meaning.** Teal is ours, violet is a third party, magenta is only
ever damage. Spend magenta on decoration and the broken edge stops being the
loudest thing on screen.
