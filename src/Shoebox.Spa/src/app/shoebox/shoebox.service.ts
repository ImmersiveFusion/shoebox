import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { FiringOutcome, FiringStatus } from './timed-firing';

export interface ParsedPod {
  id: string;
  label: string;
  serviceName: string;
  kind: string;
  replicas: number;
  pinnedInstance: number | null;
}

export interface ParsedCall {
  from: string;
  to: string;
  broken: boolean;
  brokenInstances: number[];
  reason: string | null;
}

export interface ParsedTopology {
  pods: ParsedPod[];
  calls: ParsedCall[];
  entry: string | null;
  notes: string[];
  /** Pods a request can arrive back at. Usually empty. */
  cyclicPods: string[];
}

export interface Hop {
  from: string;
  to: string;
  failed: boolean;
  ms: number;
}

/**
 * An edge in the diagram that this run crossed nowhere at all.
 *
 * Ordinarily empty, cycles included: declining is per causal path, so an arrow
 * refused on one path is normally crossed on another. Non-empty means part of
 * what somebody drew genuinely did not run, which is the one case the picture
 * has to show differently — otherwise every arrow looks alike and a run that
 * walked two thirds of the diagram looks exactly like one that walked all of it.
 */
export interface NotTaken {
  from: string;
  to: string;
  reason: string;
}

export interface RunResult {
  runIndex: number;
  traceId: string | null;
  servedBy: string[];
  spanCount: number;
  failedSpanCount: number;
  notes: string[];
  /** The edges this run crossed, in order, for replaying it on the diagram. */
  hops: Hop[];
  /** Edges in the diagram this run crossed nowhere. Usually empty. */
  notTaken: NotTaken[];
}

export interface OtlpStatus {
  configured: boolean;
  endpoint: string | null;
  hint: string;
}

@Injectable({ providedIn: 'root' })
export class ShoeboxService {
  private readonly http = inject(HttpClient);

  parse(diagram: string): Observable<ParsedTopology> {
    return this.http.post<ParsedTopology>('/topology/parse', { diagram });
  }

  /** Fires exactly one request. Nothing moves unless the user asks. */
  run(diagram: string, runIndex: number, shoeboxId: string): Observable<RunResult> {
    return this.http.post<RunResult>(`/run?shoeboxId=${encodeURIComponent(shoeboxId)}`, {
      diagram,
      runIndex,
    });
  }

  /** Lets the user tell an unconfigured endpoint from a broken diagram. */
  otlpStatus(): Observable<OtlpStatus> {
    return this.http.get<OtlpStatus>('/otlp/status');
  }

  createShoebox(): Observable<{ shoeboxId: string }> {
    return this.http.post<{ shoeboxId: string }>('/shoebox', {});
  }

  /**
   * Fires the diagram on a timer. A duration is not optional: the server refuses
   * a timer without one and caps it at two hours, so it always stops by itself.
   */
  startFiring(
    diagram: string,
    shoeboxId: string,
    durationSeconds: number,
    intervalSeconds: number,
  ): Observable<FiringOutcome> {
    return this.http.post<FiringOutcome>(this.fireUrl('/fire', shoeboxId), {
      diagram,
      durationSeconds,
      intervalSeconds,
    });
  }

  /** Whether this shoebox is firing on a timer, and how far along it is. */
  firingStatus(shoeboxId: string): Observable<FiringStatus | { firing: false }> {
    return this.http.get<FiringStatus | { firing: false }>(this.fireUrl('/fire', shoeboxId));
  }

  /** The diagram the timer's next run walks. */
  updateFiringDiagram(diagram: string, shoeboxId: string): Observable<FiringOutcome> {
    return this.http.put<FiringOutcome>(this.fireUrl('/fire/diagram', shoeboxId), { diagram });
  }

  /** More time, capped by the server so the time left never passes two hours. */
  extendFiring(shoeboxId: string, seconds: number): Observable<FiringOutcome> {
    return this.http.post<FiringOutcome>(this.fireUrl('/fire/extend', shoeboxId), { seconds });
  }

  stopFiring(shoeboxId: string): Observable<{ firing: false; stopped: boolean }> {
    return this.http.delete<{ firing: false; stopped: boolean }>(this.fireUrl('/fire', shoeboxId));
  }

  private fireUrl(path: string, shoeboxId: string): string {
    return `${path}?shoeboxId=${encodeURIComponent(shoeboxId)}`;
  }
}
