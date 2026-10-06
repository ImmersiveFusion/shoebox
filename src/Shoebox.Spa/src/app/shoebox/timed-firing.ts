/**
 * Timed firing, the parts that are not Angular: the choices offered, and how a
 * running timer is described on screen.
 *
 * There is no "until I stop it" choice, on purpose. The server refuses a timer
 * without a duration and caps it at two hours, and the UI does not offer what the
 * server would refuse.
 */

/** What the server says about a running timer. */
export interface FiringStatus {
  firing: boolean;
  shoeboxId: string;
  startedAt: string;
  stopsAt: string;
  remainingSeconds: number;
  intervalSeconds: number;
  runs: number;
  skipped: number;
  lastTraceId: string | null;
  lastError: string | null;
}

/** The server's answer to start, extend and edit. */
export interface FiringOutcome {
  status: FiringStatus;
  clamped: boolean;
  message: string | null;
}

export interface DurationChoice {
  label: string;
  seconds: number;
}

export const DURATIONS: readonly DurationChoice[] = [
  { label: '5 minutes', seconds: 5 * 60 },
  { label: '15 minutes', seconds: 15 * 60 },
  { label: '30 minutes', seconds: 30 * 60 },
  { label: '1 hour', seconds: 60 * 60 },
  { label: '2 hours', seconds: 2 * 60 * 60 },
];

export const DEFAULT_DURATION_SECONDS = 15 * 60;

/** How much one press of the extend button adds. The server caps the total. */
export const EXTEND_SECONDS = 15 * 60;

export const MAX_DURATION_SECONDS = 2 * 60 * 60;

export const DEFAULT_INTERVAL_SECONDS = 5;
export const MIN_INTERVAL_SECONDS = 1;
export const MAX_INTERVAL_SECONDS = 60;

/**
 * Pulls whatever was typed into the rate box into the range the server accepts,
 * so a stray 0 or 600 becomes a working timer rather than a refusal. The server
 * enforces the same bounds regardless.
 */
export function clampInterval(value: number | null | undefined): number {
  if (value === null || value === undefined || !Number.isFinite(value)) return DEFAULT_INTERVAL_SECONDS;
  return Math.min(MAX_INTERVAL_SECONDS, Math.max(MIN_INTERVAL_SECONDS, value));
}

/** 9:05, or 1:02:03 past an hour. */
export function formatRemaining(seconds: number): string {
  const total = Math.max(0, Math.round(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

/** The stop time in the viewer's own clock, hours and minutes. */
export function formatStopsAt(iso: string, locale?: string): string {
  return new Date(iso).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' });
}

/** The one line shown while a timer is running. */
export function describeFiring(status: FiringStatus, locale?: string): string {
  const runs = status.runs === 1 ? '1 run' : `${status.runs} runs`;
  return `Firing every ${status.intervalSeconds} s: ${runs} so far, ` +
    `stops at ${formatStopsAt(status.stopsAt, locale)} (in ${formatRemaining(status.remainingSeconds)})`;
}
