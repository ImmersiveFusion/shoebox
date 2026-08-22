import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { HttpClient } from '@angular/common/http';

// SQL scenarios - all use hardcoded queries on backend (no SQL injection possible)
export type SqlScenario = 'success' | 'wrong-table' | 'wrong-column' | 'syntax-error' | 'division-error';

// Redis scenarios
export type RedisScenario = 'success' | 'missing-key' | 'large-value' | 'expired-key' | 'serialization-error' | 'invalid-operation';

// Pipeline scenarios (elaborate telemetry generation)
export type PipelineScenario = 'simple-saga' | 'multi-replica-saga';

export interface FlowScenario {
  id: string;
  label: string;
  description: string;
  expectsError: boolean;
}

export const SQL_SCENARIOS: FlowScenario[] = [
  { id: 'success', label: 'Roundtrip', description: 'Normal query execution - full roundtrip', expectsError: false },
  { id: 'wrong-table', label: 'Wrong Table', description: 'Query references non-existent table', expectsError: true },
  { id: 'wrong-column', label: 'Wrong Column', description: 'Query references non-existent column', expectsError: true },
  { id: 'syntax-error', label: 'Syntax Error', description: 'Malformed SQL syntax', expectsError: true },
  { id: 'division-error', label: 'Division Error', description: 'Division by zero error', expectsError: true },
];

export const REDIS_SCENARIOS: FlowScenario[] = [
  { id: 'success', label: 'Roundtrip', description: 'Normal cache operation - full roundtrip', expectsError: false },
  { id: 'missing-key', label: 'Missing Key', description: 'Get non-existent key (returns null)', expectsError: false },
  { id: 'large-value', label: 'Large Value', description: 'Store 10KB payload', expectsError: false },
  { id: 'expired-key', label: 'Expired Key', description: 'Key expires immediately', expectsError: false },
  { id: 'serialization-error', label: 'Serialization Error', description: 'Corrupt data triggers error', expectsError: true },
  { id: 'invalid-operation', label: 'Invalid Operation', description: 'Wrong data type operation', expectsError: true },
];

// Pipeline Saga scenarios - simulates distributed microservices:
// - order-service: Handles order creation and management
// - inventory-service: Manages stock levels and reservations
// - payment-service: Processes payment transactions
// - notification-service: Sends order confirmations and alerts
export const PIPELINE_SCENARIOS: FlowScenario[] = [
  { id: 'simple-saga', label: 'Simple Saga', description: '4 microservices × 1 instance each = 4 spans', expectsError: false },
  { id: 'multi-replica-saga', label: 'Multi-Replica Saga', description: '4 microservices × 2 replicas each = 8 spans', expectsError: false },
];

@Injectable({
  providedIn: 'root'
})
export class FlowService {

  constructor(private httpClient: HttpClient) { }

  executeSql(sandboxId: string, scenario: SqlScenario = 'success'): Observable<any> {
    return this.httpClient.post<any>(
      `${environment.apiUri}/flow/execute/sql?sandboxId=${sandboxId}&scenario=${scenario}`,
      {}
    );
  }

  executeRedis(sandboxId: string, scenario: RedisScenario = 'success'): Observable<any> {
    return this.httpClient.post<any>(
      `${environment.apiUri}/flow/execute/redis?sandboxId=${sandboxId}&scenario=${scenario}`,
      {}
    );
  }

  executePipeline(sandboxId: string, scenario: PipelineScenario = 'simple-saga'): Observable<any> {
    return this.httpClient.post<any>(
      `${environment.apiUri}/flow/execute/pipeline?sandboxId=${sandboxId}&scenario=${scenario}`,
      {}
    );
  }
}
