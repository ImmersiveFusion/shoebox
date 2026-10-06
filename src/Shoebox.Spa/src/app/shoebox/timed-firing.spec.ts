import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ShoeboxService } from './shoebox.service';
import {
  DURATIONS,
  FiringStatus,
  MAX_DURATION_SECONDS,
  clampInterval,
  describeFiring,
  formatRemaining,
} from './timed-firing';

const status = (overrides: Partial<FiringStatus> = {}): FiringStatus => ({
  firing: true,
  shoeboxId: 'box-1',
  startedAt: '2026-01-01T12:00:00Z',
  stopsAt: '2026-01-01T12:15:00Z',
  remainingSeconds: 545,
  intervalSeconds: 5,
  runs: 12,
  skipped: 0,
  lastTraceId: null,
  lastError: null,
  ...overrides,
});

describe('timed firing choices', () => {
  it('offers no open-ended choice, and nothing past two hours', () => {
    expect(DURATIONS.length).toBeGreaterThan(0);
    for (const choice of DURATIONS) {
      expect(choice.seconds).toBeGreaterThan(0);
      expect(choice.seconds).toBeLessThanOrEqual(MAX_DURATION_SECONDS);
    }
  });

  it('pulls the rate into one to sixty seconds, and defaults to five', () => {
    expect(clampInterval(0)).toBe(1);
    expect(clampInterval(600)).toBe(60);
    expect(clampInterval(7)).toBe(7);
    expect(clampInterval(null)).toBe(5);
    expect(clampInterval(Number.NaN)).toBe(5);
  });
});

describe('describing a running timer', () => {
  it('says it is firing, how many runs, and when it stops', () => {
    const text = describeFiring(status(), 'en-GB');

    expect(text).toContain('Firing every 5 s');
    expect(text).toContain('12 runs so far');
    expect(text).toContain('stops at');
    expect(text).toContain('in 9:05');
  });

  it('counts one run as a run', () => {
    expect(describeFiring(status({ runs: 1 }), 'en-GB')).toContain('1 run so far');
  });

  it('formats time left past an hour with hours', () => {
    expect(formatRemaining(3723)).toBe('1:02:03');
    expect(formatRemaining(-4)).toBe('0:00');
  });
});

describe('ShoeboxService timed firing calls', () => {
  let service: ShoeboxService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ShoeboxService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('always sends a duration when starting', () => {
    service.startFiring('flowchart LR\n a --> b', 'box 1', 900, 5).subscribe();

    const req = http.expectOne('/fire?shoeboxId=box%201');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ diagram: 'flowchart LR\n a --> b', durationSeconds: 900, intervalSeconds: 5 });
    req.flush({ status: status(), clamped: false, message: null });
  });

  it('sends edits, extensions and stops to the same shoebox', () => {
    service.updateFiringDiagram('flowchart LR\n a -->|broken| b', 'box-1').subscribe();
    service.extendFiring('box-1', 900).subscribe();
    service.stopFiring('box-1').subscribe();

    const edit = http.expectOne(r => r.url === '/fire/diagram?shoeboxId=box-1');
    expect(edit.request.method).toBe('PUT');
    edit.flush({ status: status(), clamped: false, message: null });

    const extend = http.expectOne('/fire/extend?shoeboxId=box-1');
    expect(extend.request.body).toEqual({ seconds: 900 });
    extend.flush({ status: status(), clamped: true, message: 'capped' });

    const stop = http.expectOne(r => r.method === 'DELETE');
    expect(stop.request.url).toBe('/fire?shoeboxId=box-1');
    stop.flush({ firing: false, stopped: true });
  });
});
