import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

const MINUTE = 60_000;

/** Never open ended. Two hours is the most anyone gets, and the code enforces it. */
export const MAX_DURATION_MS = 120 * MINUTE;
export const DURATION_CHOICES_MIN = [5, 15, 30, 60, 120] as const;
export const EXTEND_MS = 15 * MINUTE;

export const MIN_INTERVAL_S = 5;
export const MAX_INTERVAL_S = 60;
export const DEFAULT_INTERVAL_S = 5;

/**
 * Fires the ordinary run call every N seconds until a chosen time, in the
 * browser and nowhere else. Close the tab and it is gone; reload and it is off.
 *
 * Each tick calls whatever it was given, which reads the diagram as it is at
 * that moment, so an edit shows up on the next run without any extra plumbing.
 */
export class TimedFiring {
  readonly running = signal(false);
  readonly runs = signal(0);
  readonly intervalS = signal(DEFAULT_INTERVAL_S);
  readonly endsAt = signal(0);
  readonly now = signal(Date.now());
  /** The last thing that went wrong, said plainly. Cleared by the next good run. */
  readonly problem = signal<string | null>(null);

  private clock: ReturnType<typeof setInterval> | null = null;
  private inFlight: Subscription | null = null;
  private lastFiredAt = 0;

  constructor(private readonly fireOnce: () => Observable<unknown>) {}

  /** Returns false, and does nothing, without a duration. */
  start(durationMs: number | null, intervalS: number): boolean {
    if (!durationMs || durationMs <= 0) return false;
    this.stop();

    const now = Date.now();
    this.intervalS.set(clamp(Math.round(intervalS) || DEFAULT_INTERVAL_S, MIN_INTERVAL_S, MAX_INTERVAL_S));
    this.endsAt.set(now + Math.min(durationMs, MAX_DURATION_MS));
    this.now.set(now);
    this.runs.set(0);
    this.lastFiredAt = 0;
    this.problem.set(null);
    this.running.set(true);

    this.tick();
    this.clock = setInterval(() => this.tick(), 1000);
    return true;
  }

  /** Fifteen more minutes, but never more than two hours from now. */
  extend(): void {
    if (!this.running()) return;
    this.endsAt.set(Math.min(this.endsAt() + EXTEND_MS, Date.now() + MAX_DURATION_MS));
  }

  stop(): void {
    if (this.clock !== null) clearInterval(this.clock);
    this.clock = null;
    this.inFlight?.unsubscribe();
    this.inFlight = null;
    this.running.set(false);
  }

  remainingMs(): number {
    return Math.max(0, this.endsAt() - this.now());
  }

  private tick(): void {
    const now = Date.now();
    this.now.set(now);

    if (now >= this.endsAt()) {
      this.stop();
      return;
    }

    if (this.lastFiredAt && now - this.lastFiredAt < this.intervalS() * 1000) return;
    this.lastFiredAt = now;

    // Skipped, not queued. A slow run must not turn into a pile of runs.
    if (this.inFlight) return;

    const sub = this.fireOnce().subscribe({
      next: () => {
        this.runs.update(n => n + 1);
        this.problem.set(null);
      },
      error: (error: unknown) => {
        this.problem.set(describe(error));
        this.inFlight = null;
      },
      complete: () => (this.inFlight = null),
    });
    // A synchronous source has already finished by now.
    this.inFlight = sub.closed ? null : sub;
  }
}

function describe(error: unknown): string {
  if (error instanceof HttpErrorResponse && error.status === 429) {
    const wait = error.headers?.get('Retry-After');
    return `Rate limited (429)${wait ? `, the server asks for ${wait} s` : ''}. Still firing, next try at the next tick.`;
  }
  if (error instanceof HttpErrorResponse) {
    return `The last run failed (${error.status || 'no response'}). Still firing.`;
  }
  return 'The last run failed. Still firing.';
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** "m:ss" for a countdown. */
export function minutesSeconds(ms: number): string {
  const total = Math.ceil(ms / 1000);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

/** "HH:MM" in local time. */
export function clockTime(epochMs: number): string {
  const d = new Date(epochMs);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
