/**
 * The URL is the save file. There is no database, no account and nothing to
 * garbage collect: a link carries the whole diagram, so a link is a runnable
 * repro.
 *
 * The diagram goes in the HASH FRAGMENT, not the query string, and that is a
 * deliberate privacy decision rather than tidiness. The pitch is "paste a diagram
 * of your system", so a meaningful share of pastes will contain real internal
 * service names. A query string is sent to the server on every request and lands
 * in access logs, CDN logs and any Referer header the browser leaks onward. A
 * fragment never leaves the browser.
 *
 * It costs nothing, because running is a POST: the server receives the diagram in
 * a request body when the user presses fire, never in a logged URL.
 *
 * The shoebox id stays in the query string, because the server does need that.
 */

const PARAM = 'd';
const SHOEBOX_PARAM = 'shoeboxId';

/** The shoebox this page is running in, if the link carried one. */
export function readShoeboxFromUrl(): string | null {
  return new URLSearchParams(window.location.search).get(SHOEBOX_PARAM);
}

/**
 * Puts the shoebox id in the address bar, so copying the URL copies the shoebox
 * along with the diagram.
 *
 * Without this the id was minted per visit and never written down, so a shared
 * link handed the next person a *different* shoebox: same diagram, same runs, and
 * `shoebox.id` on their spans not matching yours, which is exactly the tag you
 * would filter a shared backend by. A link is meant to be a runnable repro, and a
 * repro whose telemetry lands somewhere you cannot see is not one.
 *
 * Query string rather than fragment, deliberately, and it is the one thing here
 * that belongs there: the server needs it on every run, and unlike the diagram it
 * is a random id that says nothing about anybody's system.
 */
export function writeShoeboxToUrl(shoeboxId: string): void {
  if (!shoeboxId) return;
  const url = new URL(window.location.href);
  if (url.searchParams.get(SHOEBOX_PARAM) === shoeboxId) return;
  url.searchParams.set(SHOEBOX_PARAM, shoeboxId);
  window.history.replaceState(null, '', url.toString());
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(value: string): Uint8Array {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

async function pipe(bytes: Uint8Array, stream: CompressionStream | DecompressionStream): Promise<Uint8Array> {
  const blob = new Blob([bytes as unknown as BlobPart]);
  const piped = blob.stream().pipeThrough(stream as ReadableWritablePair<Uint8Array, Uint8Array>);
  const buffer = await new Response(piped).arrayBuffer();
  return new Uint8Array(buffer);
}

/** Deflate then base64url. A ten line diagram lands around 200 to 300 characters. */
export async function encodeDiagram(diagram: string): Promise<string> {
  const raw = new TextEncoder().encode(diagram);
  const deflated = await pipe(raw, new CompressionStream('deflate-raw'));
  return toBase64Url(deflated);
}

export async function decodeDiagram(encoded: string): Promise<string | null> {
  try {
    const inflated = await pipe(fromBase64Url(encoded), new DecompressionStream('deflate-raw'));
    return new TextDecoder().decode(inflated);
  } catch {
    // A mangled link is a shrug, never a crash. Somebody's mail client wrapped it.
    return null;
  }
}

export function readDiagramFromUrl(): Promise<string | null> {
  const hash = window.location.hash.replace(/^#/, '');
  const value = new URLSearchParams(hash).get(PARAM);
  return value ? decodeDiagram(value) : Promise.resolve(null);
}

export async function writeDiagramToUrl(diagram: string): Promise<void> {
  const encoded = await encodeDiagram(diagram);
  const url = new URL(window.location.href);
  url.hash = `${PARAM}=${encoded}`;
  // replaceState so typing does not fill the back button with keystrokes.
  window.history.replaceState(null, '', url.toString());
}

/**
 * Past roughly 8000 characters a link starts failing silently in mail clients and
 * chat apps. Warn in the UI rather than handing someone a link that does not work.
 */
export const URL_LENGTH_WARNING = 8000;
