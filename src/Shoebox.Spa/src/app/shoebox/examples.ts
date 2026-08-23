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

/**
 * One axis: the kind of system the example is about.
 *
 * The previous grouping was SQL, Redis, Saga, Worker, which is two vendors, a
 * flow pattern and a role. Four different axes, so nothing new had an obvious
 * home and none of the four words told a student anything.
 *
 * Order here is the order they render, and adding a group is a line in this list.
 */
/** What firing this example will do. Read off the diagram, never hand-set. */
export type Outcome = 'healthy' | 'always' | 'sometimes';

/**
 * The diagram is the whole state, so it is also the whole truth about the
 * outcome. Deriving this rather than storing it means a new example cannot claim
 * to break something it does not break, which is exactly the drift that left
 * "cache miss" and "expired key" drawing a plain healthy roundtrip.
 */
export function outcomeOf(example: Example): Outcome {
  if (/broken on #/.test(example.diagram)) return 'sometimes';
  if (/\|\s*broken/.test(example.diagram)) return 'always';
  return 'healthy';
}

export const GROUPS = ['Databases', 'Workflows', 'Distributed systems'] as const;

export const EXAMPLES: readonly Example[] = [
  // --- SQL: one picture, five outcomes -------------------------------------
  { id: 'sql-success', group: 'Databases', label: 'Datastore roundtrip',
    description: 'Normal query execution, full roundtrip', diagram: SQL('') },
  { id: 'sql-wrong-table', group: 'Databases', label: 'Wrong table',
    description: 'Query references a non-existent table',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: wrong table| db[(SQL Server)]` },
  { id: 'sql-wrong-column', group: 'Databases', label: 'Wrong column',
    description: 'Query references a non-existent column',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: wrong column| db[(SQL Server)]` },
  { id: 'sql-syntax-error', group: 'Databases', label: 'Syntax error',
    description: 'Malformed SQL syntax',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: syntax error| db[(SQL Server)]` },
  { id: 'sql-division-error', group: 'Databases', label: 'Division by zero',
    description: 'Division by zero error',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: division by zero| db[(SQL Server)]` },

  // --- Redis ----------------------------------------------------------------
  { id: 'redis-success', group: 'Databases', label: 'Cache roundtrip',
    description: 'Normal cache operation, full roundtrip', diagram: REDIS('') },
  { id: 'redis-missing-key', group: 'Databases', label: 'Cache miss',
    description: 'Get a non-existent key, returns null', diagram: REDIS('') },
  { id: 'redis-large-value', group: 'Databases', label: 'Large payload',
    description: 'Store a 10KB payload', diagram: REDIS('') },
  { id: 'redis-expired-key', group: 'Databases', label: 'Expired key',
    description: 'Key expires immediately', diagram: REDIS('') },
  { id: 'redis-serialization-error', group: 'Databases', label: 'Corrupt value',
    description: 'Corrupt data triggers an error',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: serialization error| cache((Redis))` },
  { id: 'redis-invalid-operation', group: 'Databases', label: 'Wrong type',
    description: 'Wrong data type operation',
    diagram: `flowchart LR\n  api[Orders API] -->|broken: invalid operation| cache((Redis))` },

  // --- Saga -----------------------------------------------------------------
  { id: 'simple-saga', group: 'Workflows', label: 'Chain of services',
    description: '4 microservices, 1 instance each, 4 spans', diagram: SAGA('') },
  { id: 'multi-replica-saga', group: 'Distributed systems', label: 'Two of every service',
    description: 'Every service runs twice. Fire repeatedly and watch which copy answers', diagram: SAGA(' x2') },

  // --- Worker permutation ---------------------------------------------------
  // The inherited thirteen never run a replica pool against more than one
  // downstream, which is the shape the replica mechanics exist for.
  { id: 'worker-happy', group: 'Workflows', label: 'Work queue',
    description: 'A job goes on a queue, one of five workers picks it up, and calls on from there',
    diagram: WORKER('-->') },
  { id: 'worker-broken', group: 'Workflows', label: 'Work queue, publish fails',
    description: 'Every worker reaches the API and none can publish. The diff is one label',
    diagram: WORKER('-->|broken: connection refused|') },
  { id: 'phantom-service', group: 'Distributed systems', label: 'A service that never answers',
    description: 'Nothing fails and nothing is red. One service is in every span except its own',
    diagram: `flowchart LR
  gw[API Gateway] --> orders[Orders API]
  orders --> inv[Inventory API]
  orders --> pay[Payments API]
  pay --> ledger[(Ledger DB)]
  pay --> cache((Rates Cache))
  orders -->|phantom| audit[Audit Service]` },

  { id: 'worker-broken-one', group: 'Distributed systems', label: 'Fails one run in five',
    description: 'Five workers, one of them broken. Four runs look perfect and the fifth does not',
    diagram: WORKER('-->|broken on #3: connection refused|') },
];

export const DEFAULT_EXAMPLE = EXAMPLES.find(e => e.id === 'worker-broken-one')!;
