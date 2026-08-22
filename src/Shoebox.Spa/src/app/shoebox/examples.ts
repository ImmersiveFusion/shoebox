/**
 * The prebaked examples.
 *
 * A picker is not a feature. Every entry here is a string that gets pasted into
 * the box, and if adding one ever requires code, the design has gone wrong.
 *
 * The first thirteen are the scenarios the old sandbox shipped, carried over so
 * nothing quietly disappears in the rewrite. Note there are thirteen scenarios but
 * only three topologies: the four SQL failures draw an identical picture and
 * differ only in how the call fails, which is why the break label carries a
 * reason after the colon.
 */
export interface Example {
  readonly id: string;
  readonly group: string;
  readonly label: string;
  readonly description: string;
  readonly diagram: string;
}

const SQL = (extra: string) => `flowchart LR
  api[Orders API] --> db[(SQL Server)]${extra}`;

const REDIS = (extra: string) => `flowchart LR
  api[Orders API] --> cache((Redis))${extra}`;

const SAGA = (suffix: string) => `flowchart LR
  gw[API Gateway] --> orders[Orders${suffix}]
  orders --> payment[Payment${suffix}]
  payment --> shipping[Shipping${suffix}]
  shipping --> notify[Notification${suffix}]`;

const WORKER = (rabbitLabel: string) => `flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> q[[Job Queue]]
  q --> worker[Worker x5]
  worker --> api[Inventory API]
  worker ${rabbitLabel} rabbit[[RabbitMQ]]`;

export const EXAMPLES: readonly Example[] = [
  // --- SQL: one picture, five outcomes -------------------------------------
  { id: 'sql-success', group: 'SQL', label: 'Roundtrip',
    description: 'Normal query execution, full roundtrip', diagram: SQL('') },
  { id: 'sql-wrong-table', group: 'SQL', label: 'Wrong Table',
    description: 'Query references a non-existent table',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: wrong table| db[(SQL Server)]` },
  { id: 'sql-wrong-column', group: 'SQL', label: 'Wrong Column',
    description: 'Query references a non-existent column',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: wrong column| db[(SQL Server)]` },
  { id: 'sql-syntax-error', group: 'SQL', label: 'Syntax Error',
    description: 'Malformed SQL syntax',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: syntax error| db[(SQL Server)]` },
  { id: 'sql-division-error', group: 'SQL', label: 'Division Error',
    description: 'Division by zero error',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: division by zero| db[(SQL Server)]` },

  // --- Redis ----------------------------------------------------------------
  { id: 'redis-success', group: 'Redis', label: 'Roundtrip',
    description: 'Normal cache operation, full roundtrip', diagram: REDIS('') },
  { id: 'redis-missing-key', group: 'Redis', label: 'Missing Key',
    description: 'Get a non-existent key, returns null', diagram: REDIS('') },
  { id: 'redis-large-value', group: 'Redis', label: 'Large Value',
    description: 'Store a 10KB payload', diagram: REDIS('') },
  { id: 'redis-expired-key', group: 'Redis', label: 'Expired Key',
    description: 'Key expires immediately', diagram: REDIS('') },
  { id: 'redis-serialization-error', group: 'Redis', label: 'Serialization Error',
    description: 'Corrupt data triggers an error',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: serialization error| cache((Redis))` },
  { id: 'redis-invalid-operation', group: 'Redis', label: 'Invalid Operation',
    description: 'Wrong data type operation',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: invalid operation| cache((Redis))` },

  // --- Saga -----------------------------------------------------------------
  { id: 'simple-saga', group: 'Saga', label: 'Simple Saga',
    description: '4 microservices, 1 instance each, 4 spans', diagram: SAGA('') },
  { id: 'multi-replica-saga', group: 'Saga', label: 'Multi-Replica Saga',
    description: '4 microservices, 2 replicas each', diagram: SAGA(' x2') },

  // --- Worker permutation ---------------------------------------------------
  // The inherited thirteen never run a replica pool against more than one
  // downstream, which is the shape the replica mechanics exist for.
  { id: 'worker-happy', group: 'Worker', label: 'Fan-out, healthy',
    description: 'One request, one worker instance, two downstream calls that both succeed',
    diagram: WORKER('-->') },
  { id: 'worker-broken', group: 'Worker', label: 'Fan-out, publish fails',
    description: 'Every worker reaches the API and none can publish. The diff is one label',
    diagram: WORKER('-->|broken: connection refused|') },
  { id: 'worker-broken-one', group: 'Worker', label: 'Fan-out, one bad instance',
    description: 'Four runs look perfect and the fifth fails. Fire it five times',
    diagram: WORKER('-->|broken on #3: connection refused|') },
];

export const DEFAULT_EXAMPLE = EXAMPLES.find(e => e.id === 'worker-broken-one')!;
