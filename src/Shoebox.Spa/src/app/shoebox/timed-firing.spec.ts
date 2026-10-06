import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { ShoeboxComponent } from './shoebox.component';
import { RunResult, ShoeboxService } from './shoebox.service';
import { MAX_DURATION_MS, RUN_TIMEOUT_MS, TimedFiring } from './timed-firing';

const SECOND = 1000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;

/** Each call returns a fresh Subject the test answers by hand, or an immediate value. */
function fakeRunner(mode: 'immediate' | 'manual' = 'immediate') {
  const pending: Subject<unknown>[] = [];
  let calls = 0;
  const fire = (): Observable<unknown> => {
    calls += 1;
    if (mode === 'immediate') return of({});
    const s = new Subject<unknown>();
    pending.push(s);
    return s;
  };
  return { fire, pending, calls: () => calls };
}

describe('TimedFiring', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('cannot start without a duration', () => {
    const runner = fakeRunner();
    const timed = new TimedFiring(runner.fire);

    expect(timed.start(null, 5)).toBe(false);
    expect(timed.start(0, 5)).toBe(false);
    vi.advanceTimersByTime(MINUTE);

    expect(timed.running()).toBe(false);
    expect(runner.calls()).toBe(0);
  });

  it('fires at once and then every N seconds, with the rate held to 5..60 s', () => {
    const runner = fakeRunner();
    const timed = new TimedFiring(runner.fire);

    timed.start(5 * MINUTE, 1);
    expect(timed.intervalS()).toBe(5);
    expect(runner.calls()).toBe(1);
    vi.advanceTimersByTime(20 * SECOND);
    expect(runner.calls()).toBe(5);
    expect(timed.runs()).toBe(5);

    timed.start(5 * MINUTE, 600);
    expect(timed.intervalS()).toBe(60);
  });

  it('fires 12 times in a minute at 5 s, even when the clock jitters', () => {
    const runner = fakeRunner();
    const timed = new TimedFiring(runner.fire);

    timed.start(5 * MINUTE, 5);
    // Real ticks wobble: here odd seconds land 1 ms early and even ones 1 ms late,
    // so a run fired on a late tick sees the next due tick arrive at 4.998 s.
    const clock = Date.now;
    const t0 = clock();
    const jitter = vi.spyOn(Date, 'now').mockImplementation(() => {
      const t = clock();
      return t + (Math.round((t - t0) / SECOND) % 2 === 0 ? 1 : -1);
    });
    vi.advanceTimersByTime(59 * SECOND);
    jitter.mockRestore();

    // 0, 5, 10 ... 55 s: twelve in the minute, not the ten a 6 s pace gives.
    expect(runner.calls()).toBe(12);
  });

  it('gives up on a run that never answers, says so, and keeps firing', () => {
    const runner = fakeRunner('manual');
    const timed = new TimedFiring(runner.fire);

    timed.start(5 * MINUTE, 5);
    vi.advanceTimersByTime(RUN_TIMEOUT_MS);
    expect(timed.problem()).toContain('did not answer');
    expect(timed.running()).toBe(true);

    vi.advanceTimersByTime(5 * SECOND);
    expect(runner.calls()).toBe(2);
  });

  it('enforces the two hour ceiling however long was asked for', () => {
    const timed = new TimedFiring(fakeRunner().fire);

    timed.start(10 * HOUR, 60);
    expect(timed.remainingMs()).toBe(MAX_DURATION_MS);

    vi.advanceTimersByTime(2 * HOUR + SECOND);
    expect(timed.running()).toBe(false);
  });

  it('stops at the end with nobody touching it', () => {
    const runner = fakeRunner();
    const timed = new TimedFiring(runner.fire);

    timed.start(5 * MINUTE, 5);
    vi.advanceTimersByTime(5 * MINUTE + SECOND);
    const atEnd = runner.calls();

    expect(timed.running()).toBe(false);
    expect(atEnd).toBe(60);
    vi.advanceTimersByTime(HOUR);
    expect(runner.calls()).toBe(atEnd);
  });

  it('stops when Stop is pressed', () => {
    const runner = fakeRunner();
    const timed = new TimedFiring(runner.fire);

    timed.start(30 * MINUTE, 5);
    vi.advanceTimersByTime(10 * SECOND);
    timed.stop();
    const atStop = runner.calls();

    vi.advanceTimersByTime(HOUR);
    expect(timed.running()).toBe(false);
    expect(runner.calls()).toBe(atStop);
  });

  it('caps +15 min so that no more than two hours ever remain', () => {
    const timed = new TimedFiring(fakeRunner().fire);

    timed.start(5 * MINUTE, 5);
    timed.extend();
    expect(timed.remainingMs()).toBe(20 * MINUTE);

    timed.start(110 * MINUTE, 5);
    timed.extend();
    expect(timed.remainingMs()).toBe(MAX_DURATION_MS);
    timed.extend();
    expect(timed.remainingMs()).toBe(MAX_DURATION_MS);
  });

  it('skips a tick while the previous run is still out, rather than queueing it', () => {
    const runner = fakeRunner('manual');
    const timed = new TimedFiring(runner.fire);

    timed.start(5 * MINUTE, 5);
    expect(runner.calls()).toBe(1);

    // Three ticks pass with the first run still in flight.
    vi.advanceTimersByTime(15 * SECOND);
    expect(runner.calls()).toBe(1);

    runner.pending[0].next({});
    runner.pending[0].complete();
    expect(timed.runs()).toBe(1);

    // One run on the next tick, not the three that were skipped.
    vi.advanceTimersByTime(5 * SECOND);
    expect(runner.calls()).toBe(2);
  });

  it('says so plainly on a 429 and keeps going at the same pace', () => {
    let calls = 0;
    const timed = new TimedFiring(() => {
      calls += 1;
      return calls === 2
        ? throwError(() => new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '30' }) }))
        : of({});
    });

    timed.start(5 * MINUTE, 5);
    vi.advanceTimersByTime(5 * SECOND);

    expect(calls).toBe(2);
    expect(timed.running()).toBe(true);
    expect(timed.problem()).toContain('429');
    expect(timed.problem()).toContain('30 s');

    vi.advanceTimersByTime(4 * SECOND);
    expect(calls).toBe(2);
    vi.advanceTimersByTime(SECOND);
    expect(calls).toBe(3);
    expect(timed.problem()).toBeNull();
    expect(timed.runs()).toBe(2);
  });
});

describe('ShoeboxComponent timed firing', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('stops when the component is destroyed', async () => {
    let runs = 0;
    const result: RunResult = {
      runIndex: 1, traceId: null, servedBy: [], spanCount: 0, failedSpanCount: 0,
      notes: [], hops: [], notTaken: [],
    };
    const service = { run: () => { runs += 1; return of(result); } };

    await TestBed.configureTestingModule({
      declarations: [ShoeboxComponent],
      providers: [{ provide: ShoeboxService, useValue: service }],
    })
      .overrideComponent(ShoeboxComponent, { set: { template: '<div #render></div>' } })
      .compileComponents();

    const fixture = TestBed.createComponent(ShoeboxComponent);
    const component = fixture.componentInstance;
    component.timedMinutes = 30;
    component.startTimed();
    vi.advanceTimersByTime(10 * SECOND);
    expect(runs).toBe(3);

    fixture.destroy();
    vi.advanceTimersByTime(HOUR);

    expect(component.timed.running()).toBe(false);
    expect(runs).toBe(3);
  });

  it('does not start without a duration picked', async () => {
    const service = { run: vi.fn() };
    await TestBed.configureTestingModule({
      declarations: [ShoeboxComponent],
      providers: [{ provide: ShoeboxService, useValue: service }],
    })
      .overrideComponent(ShoeboxComponent, { set: { template: '<div #render></div>' } })
      .compileComponents();

    const component = TestBed.createComponent(ShoeboxComponent).componentInstance;
    component.startTimed();

    expect(component.timed.running()).toBe(false);
    expect(service.run).not.toHaveBeenCalled();
  });
});
