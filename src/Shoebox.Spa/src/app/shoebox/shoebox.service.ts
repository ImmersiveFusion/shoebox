import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

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
}

export interface Hop {
  from: string;
  to: string;
  failed: boolean;
  ms: number;
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
}
