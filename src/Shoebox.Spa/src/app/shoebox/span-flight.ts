/**
 * Flies a dot along the path a request actually took.
 *
 * The old chaos sandbox did this with hardcoded node coordinates and a linear
 * interpolation between two points, which worked because its diagram was a fixed
 * hand-drawn SVG. Nothing here is fixed: the user pastes a topology and mermaid
 * lays it out. So the dot follows the real edge instead.
 *
 * Mermaid gives every edge a stable id of the form `L_{from}_{to}_{n}`, and an
 * SVG path knows its own length and the point at any distance along it. Between
 * those two facts the dot can ride any curve mermaid draws, in any topology,
 * with no knowledge of where anything ended up.
 */

/** One edge crossed, as the server reported it. */
export interface Hop {
  from: string;
  to: string;
  failed: boolean;
  ms: number;
}

/**
 * Modelled milliseconds are honest but unwatchable: a five hop run is about 46ms
 * end to end, which is three frames. Slowed by a constant so the relative timings
 * survive, a third party still visibly costs more than a cache, and a person can
 * actually see it happen.
 */
const SLOWDOWN = 14;

/** Below this a hop is a flash rather than a movement, whatever the model says. */
const MIN_HOP_MS = 220;

const TEAL = '#5df2e2';
const RED = '#ff4438';

/**
 * Nothing here is allowed to break the diagram. Every entry point is wrapped, and
 * a failure leaves the picture exactly as it was: this is decoration on top of the
 * thing that matters, and it never gets to take that thing down with it.
 */
export function flyRun(host: HTMLElement, hops: readonly Hop[]): () => void {
  try {
    return run(host, hops);
  } catch {
    return () => undefined;
  }
}

function run(host: HTMLElement, hops: readonly Hop[]): () => void {
  const svg = host.querySelector('svg');
  if (!svg || hops.length === 0) return () => undefined;

  if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
    return () => undefined;
  }

  const layer = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  layer.setAttribute('class', 'span-flight');
  layer.setAttribute('pointer-events', 'none');
  svg.appendChild(layer);

  let frame = 0;
  let cancelled = false;
  const stop = () => {
    cancelled = true;
    if (frame) cancelAnimationFrame(frame);
    layer.remove();
  };

  const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
  dot.setAttribute('r', '5');
  dot.setAttribute('fill', TEAL);
  dot.setAttribute('filter', 'url(#shoebox-photon)');
  ensureGlow(svg);
  layer.appendChild(dot);

  let index = 0;
  let startedAt = 0;

  const step = (now: number) => {
    if (cancelled) return;

    const hop = hops[index];
    const path = edgePath(svg, hop);

    // An edge with no path is a diagram the parser read differently from the way
    // mermaid drew it. Skip that hop rather than stopping the whole flight.
    if (!path) {
      if (++index >= hops.length) return stop();
      startedAt = 0;
      frame = requestAnimationFrame(step);
      return;
    }

    if (startedAt === 0) startedAt = now;
    const duration = Math.max(hop.ms * SLOWDOWN, MIN_HOP_MS);
    const t = Math.min((now - startedAt) / duration, 1);

    // A failing call does not arrive. The dot gets most of the way and stops,
    // which is what a refused connection looks like from the caller's end.
    const reach = hop.failed ? 0.72 : 1;
    const point = path.getPointAtLength(path.getTotalLength() * t * reach);

    dot.setAttribute('cx', String(point.x));
    dot.setAttribute('cy', String(point.y));
    dot.setAttribute('fill', hop.failed ? RED : TEAL);

    if (t < 1) {
      frame = requestAnimationFrame(step);
      return;
    }

    if (hop.failed) {
      burst(layer, point);
      // The run is over. Whatever came after this call never happened.
      window.setTimeout(() => { if (!cancelled) stop(); }, 420);
      return;
    }

    if (++index >= hops.length) {
      window.setTimeout(() => { if (!cancelled) stop(); }, 160);
      return;
    }

    startedAt = 0;
    frame = requestAnimationFrame(step);
  };

  frame = requestAnimationFrame(step);
  return stop;
}

/**
 * Mermaid ids its edges `L_{from}_{to}_{n}`, prefixed with the diagram id. The
 * index disambiguates parallel edges between the same pair, and the first one is
 * the right answer for a run that crossed that pair once.
 */
function edgePath(svg: SVGElement, hop: Hop): SVGPathElement | null {
  const escaped = (v: string) => v.replace(/["\\]/g, '\\$&');
  const direct = svg.querySelector<SVGPathElement>(
    `path[id$="L_${escaped(hop.from)}_${escaped(hop.to)}_0"]`,
  );
  if (direct) return direct;

  // Fall back to any index, for a diagram that draws the pair more than once.
  return svg.querySelector<SVGPathElement>(
    `path[id*="L_${escaped(hop.from)}_${escaped(hop.to)}_"]`,
  );
}

/** What a refused call looks like: it stops, and it is the only red on screen. */
function burst(layer: SVGGElement, at: DOMPoint): void {
  const ring = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
  ring.setAttribute('cx', String(at.x));
  ring.setAttribute('cy', String(at.y));
  ring.setAttribute('r', '5');
  ring.setAttribute('fill', 'none');
  ring.setAttribute('stroke', RED);
  ring.setAttribute('stroke-width', '2');
  ring.setAttribute('class', 'span-flight-burst');
  layer.appendChild(ring);
}

/**
 * The glow is a filter rather than a shadow because it has to live inside the
 * SVG. Defined once per render and reused; mermaid replaces the whole element on
 * every re-render, so this quietly comes back with it.
 */
function ensureGlow(svg: SVGElement): void {
  if (svg.querySelector('#shoebox-photon')) return;

  const ns = 'http://www.w3.org/2000/svg';
  const defs = svg.querySelector('defs') ?? svg.insertBefore(document.createElementNS(ns, 'defs'), svg.firstChild);
  const filter = document.createElementNS(ns, 'filter');
  filter.setAttribute('id', 'shoebox-photon');
  filter.setAttribute('x', '-120%');
  filter.setAttribute('y', '-120%');
  filter.setAttribute('width', '340%');
  filter.setAttribute('height', '340%');

  const blur = document.createElementNS(ns, 'feGaussianBlur');
  blur.setAttribute('stdDeviation', '3.2');
  blur.setAttribute('result', 'glow');

  const merge = document.createElementNS(ns, 'feMerge');
  for (const input of ['glow', 'SourceGraphic']) {
    const node = document.createElementNS(ns, 'feMergeNode');
    node.setAttribute('in', input);
    merge.appendChild(node);
  }

  filter.appendChild(blur);
  filter.appendChild(merge);
  defs.appendChild(filter);
}
