import { Component, EventEmitter, Input, OnInit, Output, OnChanges, ElementRef, ViewChild, AfterViewInit, ChangeDetectorRef, ChangeDetectionStrategy } from '@angular/core';
import { networkDiagramAnimations } from './network-diagram.animations';
import { SQL_SCENARIOS, REDIS_SCENARIOS, PIPELINE_SCENARIOS, FlowScenario, SqlScenario, RedisScenario, PipelineScenario } from '../../services/flow.service';

export interface FlowRequest {
  resource: string;
  scenario: string;
}

export interface NetworkNode {
  id: string;
  type: 'client' | 'api' | 'database' | 'cache' | 'telemetry' | 'message-broker' | 'message-worker';
  label: string;
  icon: string;
  status: 'idle' | 'processing' | 'success' | 'error';
  comingSoon?: boolean;
}

export interface Connection {
  id: string;
  from: string;
  to: string;
  resource: string;
  status: 'healthy' | 'broken';
}

export interface RequestFlow {
  id: string;
  resource: string;
  status: 'idle' | 'traveling-to-api' | 'processing' | 'traveling-to-target' | 'returning' | 'success' | 'error';
  progress: number;
}

// SVG coordinate positions for each node (matches SVG line endpoints)
// Based on viewBox="0 -30 920 700" - aligned with HTML node positions
const NODE_POSITIONS: { [key: string]: { x: number; y: number } } = {
  'client': { x: 495, y: 210 },   // Developer workstation right edge
  'api': { x: 690, y: 210 },      // Center of sandbox
  'sql': { x: 900, y: 180 },      // Right side, above API
  'redis': { x: 900, y: 290 },    // Right side, below API
  'message-broker': { x: 900, y: 10 },    // Right side, top
  'message-worker': { x: 690, y: 10 },    // Center, top
  'otel': { x: 690, y: 415 },     // Center, below API
  'immersive-apm': { x: 610, y: 510 },  // IAPM Cloud (45° down-left from OTel)
  'immersive-apm-client': { x: 385, y: 130 },  // IAPM Desktop in dev workstation
  'others': { x: 770, y: 510 },         // Other Plumbing (45° down-right from OTel)
  'others-client': { x: 385, y: 280 }   // Others client (y is dynamic, see getOtherToolsY)
};

@Component({
  selector: 'app-network-diagram',
  templateUrl: './network-diagram.component.html',
  styleUrls: ['./network-diagram.component.scss'],
  animations: networkDiagramAnimations,
  changeDetection: ChangeDetectionStrategy.Eager,
  standalone: false
})
export class NetworkDiagramComponent implements OnInit, OnChanges, AfterViewInit {
  @Input() resources: { [id: string]: boolean } = {};
  @Output() connectionToggle = new EventEmitter<string>();
  @Output() executeFlow = new EventEmitter<FlowRequest>();
  @Output() logMessage = new EventEmitter<string>();

  @ViewChild('diagramContainer') diagramContainer!: ElementRef<HTMLDivElement>;

  // Scenario configuration
  sqlScenarios: FlowScenario[] = SQL_SCENARIOS;
  redisScenarios: FlowScenario[] = REDIS_SCENARIOS;
  pipelineScenarios: FlowScenario[] = PIPELINE_SCENARIOS;
  selectedSqlScenario: SqlScenario = 'success';
  selectedRedisScenario: RedisScenario = 'success';
  selectedPipelineScenario: PipelineScenario = 'simple-saga';
  sqlExpanded = false;
  redisExpanded = false;
  pipelineExpanded = false;

  // Explanation card expansion states
  sqlExplanationExpanded = false;
  redisExplanationExpanded = false;
  pipelineExplanationExpanded = false;

  // Nodes
  nodes: NetworkNode[] = [
    { id: 'client', type: 'client', label: 'Client', icon: 'fa-user', status: 'idle' },
    { id: 'api', type: 'api', label: 'API', icon: 'fa-server', status: 'idle' },
    { id: 'sql', type: 'database', label: 'SQL Database', icon: 'fa-database', status: 'idle' },
    { id: 'redis', type: 'cache', label: 'Redis Cache', icon: 'fa-bolt', status: 'idle' },
    { id: 'message-broker', type: 'message-broker', label: 'Message Broker', icon: 'fa-exchange-alt', status: 'idle', comingSoon: true },
    { id: 'message-worker', type: 'message-worker', label: 'Worker', icon: 'fa-cogs', status: 'idle', comingSoon: true },
    { id: 'otel', type: 'telemetry', label: 'OpenTelemetry', icon: 'fa-chart-line', status: 'idle' },
    { id: 'immersive-apm', type: 'telemetry', label: 'Immersive APM', icon: 'fa-wave-square', status: 'idle' },
    { id: 'others', type: 'telemetry', label: 'Others', icon: 'fa-ellipsis-h', status: 'idle' }
  ];

  // Connections
  connections: Connection[] = [
    { id: 'client-api', from: 'client', to: 'api', resource: 'api', status: 'healthy' },
    { id: 'api-sql', from: 'api', to: 'sql', resource: 'sql', status: 'healthy' },
    { id: 'api-redis', from: 'api', to: 'redis', resource: 'redis', status: 'healthy' },
    { id: 'api-otel', from: 'api', to: 'otel', resource: 'otel', status: 'healthy' },
    { id: 'otel-immersive', from: 'otel', to: 'immersive-apm', resource: 'immersive-apm', status: 'healthy' },
    { id: 'otel-others', from: 'otel', to: 'others', resource: 'others', status: 'healthy' }
  ];

  // Current request flow state
  currentFlow: RequestFlow | null = null;
  showExplosion = false;
  explosionNodeId = '';

  // Visibility toggles
  showOptionalCapabilities = false;
  showComingSoon = false;

  // Animation state
  isAnimating = false;
  allowConcurrentRequests = false; // When true, allows multiple requests during animation

  // Fullscreen state
  isFullscreen = false;

  // Request dot animation
  requestDotVisible = false;
  requestDotPosition = { fromNode: '', toNode: '', progress: 0 };
  requestDotClass = '';

  // Telemetry animation state
  telemetryDotVisible = false;
  telemetryDotProgress = 0;

  // APM dots animation state (from OpenTelemetry to destinations)
  immersiveApmDotVisible = false;
  immersiveApmDotProgress = 0;
  othersDotVisible = false;
  othersDotProgress = 0;

  // Optional telemetry dots (SQL and Redis to OpenTelemetry)
  sqlTelemetryDotVisible = false;
  sqlTelemetryDotProgress = 0;
  redisTelemetryDotVisible = false;
  redisTelemetryDotProgress = 0;

  // Client dots (from APM backends to developer workstation)
  iapmDesktopDotVisible = false;
  iapmDesktopDotProgress = 0;
  iapmWebDotVisible = false;
  iapmWebDotProgress = 0;
  othersClientDotVisible = false;
  othersClientDotProgress = 0;

  // Status ticker - messages stack horizontally and linger
  statusMessages: { id: number; text: string; type: 'request' | 'telemetry'; step: number }[] = [];
  private messageIdCounter = 0;
  private stepCounter = 0;
  private lastDisappearTime = 0; // Track when the last message is scheduled to disappear

  constructor(private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.syncConnectionStates();
  }

  ngAfterViewInit(): void {
    // DOM is ready
  }

  ngOnChanges(): void {
    this.syncConnectionStates();
  }

  private syncConnectionStates(): void {
    this.connections.forEach(conn => {
      if (conn.resource === 'sql' || conn.resource === 'redis') {
        conn.status = this.resources[conn.resource] ? 'broken' : 'healthy';
      }
    });
  }

  getNode(id: string): NetworkNode | undefined {
    return this.nodes.find(n => n.id === id);
  }

  getConnection(id: string): Connection | undefined {
    return this.connections.find(c => c.id === id);
  }

  getConnectionForResource(resource: string): Connection | undefined {
    return this.connections.find(c => c.resource === resource);
  }

  onConnectionClick(connection: Connection): void {
    console.log('Connection clicked:', connection);
    // Allow toggling connections even during animations
    if (connection.resource === 'api' || connection.resource === 'otel') return;

    console.log('Emitting connectionToggle for:', connection.resource);
    this.connectionToggle.emit(connection.resource);
  }

  onSendRequest(resource: string): void {
    // Block concurrent requests unless explicitly allowed
    if (!this.allowConcurrentRequests && (this.isAnimating || this.currentFlow)) return;

    this.isAnimating = true;
    let scenario: string;
    if (resource === 'sql') {
      scenario = this.selectedSqlScenario;
    } else if (resource === 'redis') {
      scenario = this.selectedRedisScenario;
    } else if (resource === 'pipeline') {
      scenario = this.selectedPipelineScenario;
    } else {
      scenario = 'success';
    }
    this.executeFlow.emit({ resource, scenario });
    this.startFlowAnimation(resource);
  }

  togglePipelineExpanded(): void {
    this.pipelineExpanded = !this.pipelineExpanded;
    if (this.pipelineExpanded) {
      this.sqlExpanded = false;
      this.redisExpanded = false;
    }
  }

  toggleSqlExpanded(): void {
    this.sqlExpanded = !this.sqlExpanded;
    if (this.sqlExpanded) {
      this.redisExpanded = false;
      this.pipelineExpanded = false;
    }
  }

  toggleRedisExpanded(): void {
    this.redisExpanded = !this.redisExpanded;
    if (this.redisExpanded) {
      this.sqlExpanded = false;
      this.pipelineExpanded = false;
    }
  }

  getSelectedScenario(resource: string): FlowScenario | undefined {
    if (resource === 'sql') {
      return this.sqlScenarios.find(s => s.id === this.selectedSqlScenario);
    } else {
      return this.redisScenarios.find(s => s.id === this.selectedRedisScenario);
    }
  }

  getSelectedScenarioLabel(resource: string): string {
    const scenario = this.getSelectedScenario(resource);
    return scenario?.label || 'Roundtrip';
  }

  getSelectedPipelineScenario(): FlowScenario | undefined {
    return this.pipelineScenarios.find(s => s.id === this.selectedPipelineScenario);
  }

  getSelectedPipelineScenarioLabel(): string {
    return this.getSelectedPipelineScenario()?.label || 'Simple Saga';
  }

  getSelectedPipelineScenarioDescription(): string {
    return this.getSelectedPipelineScenario()?.description || '';
  }

  getClientNodeHeight(): number {
    const baseHeight = 110; // Header section + padding
    const sqlHeight = this.sqlExpanded ? 155 : 65; // Expanded vs collapsed
    const redisHeight = this.redisExpanded ? 175 : 65; // Redis has more options
    const pipelineHeight = this.pipelineExpanded ? 115 : 65; // Pipeline section with 2 options when expanded
    return baseHeight + sqlHeight + redisHeight + pipelineHeight;
  }

  getOtherToolsY(): number {
    // Bottom-align Other Tools inside Developer Workstation
    // Dev Workstation: starts at y=100, height = getClientNodeHeight() + 50
    // Other Tools height = 70, 10px padding from bottom
    return 45 + this.getClientNodeHeight() + 50 - 70 - 10;
  }

  private addStatusMessage(text: string, type: 'request' | 'telemetry' = 'request'): void {
    const id = ++this.messageIdCounter;
    const step = ++this.stepCounter;
    this.statusMessages.push({ id, text, type, step });
    this.cdr.detectChanges();

    // Sequential disappearance - each message disappears 800ms after the previous one
    const now = Date.now();
    const baseDelay = 1500; // Minimum time a message stays visible
    const staggerDelay = 800; // Time between each message disappearing

    // Schedule this message to disappear after the last one, or after baseDelay if first
    const disappearTime = Math.max(now + baseDelay, this.lastDisappearTime + staggerDelay);
    this.lastDisappearTime = disappearTime;

    const delay = disappearTime - now;
    setTimeout(() => {
      this.statusMessages = this.statusMessages.filter(m => m.id !== id);
      this.cdr.detectChanges();
    }, delay);
  }

  private resetStepCounters(): void {
    this.stepCounter = 0;
  }

  get requestMessages() {
    return this.statusMessages.filter(m => m.type === 'request');
  }

  get telemetryMessages() {
    return this.statusMessages.filter(m => m.type === 'telemetry');
  }

  private async startFlowAnimation(resource: string): Promise<void> {
    // Handle pipeline specially - it's a multi-resource flow
    if (resource === 'pipeline') {
      await this.startPipelineFlowAnimation();
      return;
    }

    const targetConnection = this.connections.find(c => c.resource === resource);
    if (!targetConnection) return;

    const isBroken = targetConnection.status === 'broken';
    const targetLabel = resource === 'sql' ? 'SQL Database' : 'Redis Cache';

    // Reset step counters for new animation
    this.resetStepCounters();

    this.currentFlow = {
      id: Date.now().toString(),
      resource,
      status: 'traveling-to-api',
      progress: 0
    };

    // Step 1: Client pulses briefly
    this.setNodeStatus('client', 'processing');
    this.addStatusMessage('Sending request to API', 'request');
    await this.delay(100);

    // Step 2: Travel to API - initialize position BEFORE making visible
    this.requestDotPosition = { fromNode: 'client', toNode: 'api', progress: 0 };
    this.requestDotClass = '';
    this.requestDotVisible = true;
    this.cdr.detectChanges();
    this.setNodeStatus('client', 'idle'); // Stop pulsing once dot starts moving
    await this.animateDot('client', 'api', 400);
    this.setNodeStatus('api', 'processing');
    this.addStatusMessage('API processing request', 'request');

    await this.delay(300);

    // Step 3: Travel to target
    this.currentFlow!.status = 'traveling-to-target';
    const targetNodeId = resource === 'sql' ? 'sql' : 'redis';
    this.addStatusMessage(`API querying ${targetLabel}`, 'request');

    if (isBroken) {
      // Animate partially then explode
      await this.animateDot('api', targetNodeId, 300, 0.6);
      this.setNodeStatus('api', 'error');
      this.addStatusMessage(`Connection to ${targetLabel} failed!`, 'request');

      // Show explosion
      this.requestDotClass = 'dot-error';
      this.showExplosion = true;
      this.explosionNodeId = targetNodeId;
      this.cdr.detectChanges();
      await this.delay(400);
      this.showExplosion = false;
      this.requestDotVisible = false;
      this.cdr.detectChanges();

      // Error state
      this.currentFlow!.status = 'error';
      this.setNodeStatus('api', 'error');
      await this.delay(300);
      this.setNodeStatus('client', 'error');
      this.addStatusMessage('Error returned to client', 'request');

      // Send telemetry on error (fire and forget)
      this.animateTelemetry();

      await this.delay(500);

      // Reset
      this.setNodeStatus('api', 'idle');
      this.setNodeStatus('client', 'idle');
    } else {
      // Success path
      await this.animateDot('api', targetNodeId, 400);
      this.setNodeStatus('api', 'idle');
      this.setNodeStatus(targetNodeId, 'success');
      this.addStatusMessage(`${targetLabel} responding`, 'request');

      // Fire optional telemetry from the target node (SQL or Redis) to OpenTelemetry
      if (resource === 'sql') {
        this.animateSqlTelemetry();
      } else if (resource === 'redis') {
        this.animateRedisTelemetry();
      }

      await this.delay(200);

      // Return journey
      this.currentFlow!.status = 'returning';
      this.requestDotClass = 'dot-success';
      this.addStatusMessage(`${targetLabel} returning data`, 'request');
      await this.animateDot(targetNodeId, 'api', 400);
      this.setNodeStatus(targetNodeId, 'idle');

      // Send telemetry when request completes at API (fire and forget)
      this.animateTelemetry();

      this.addStatusMessage('API responding to client', 'request');
      await this.animateDot('api', 'client', 400);
      this.setNodeStatus('client', 'success');
      this.addStatusMessage('Request completed', 'request');
      await this.delay(300);

      this.currentFlow!.status = 'success';
      this.setNodeStatus('client', 'idle');
      this.requestDotVisible = false;
    }

    this.currentFlow = null;
    this.isAnimating = false;
  }

  private async startPipelineFlowAnimation(): Promise<void> {
    // Reset step counters for new animation
    this.resetStepCounters();

    this.currentFlow = {
      id: Date.now().toString(),
      resource: 'pipeline',
      status: 'traveling-to-api',
      progress: 0
    };

    // Stage 1: Client sends request
    this.setNodeStatus('client', 'processing');
    this.addStatusMessage('Pipeline: Client initiating', 'request');
    await this.delay(100);

    // Travel to API
    this.requestDotPosition = { fromNode: 'client', toNode: 'api', progress: 0 };
    this.requestDotClass = '';
    this.requestDotVisible = true;
    this.cdr.detectChanges();
    this.setNodeStatus('client', 'idle');
    await this.animateDot('client', 'api', 300);

    // Stage 2: API Validation
    this.setNodeStatus('api', 'processing');
    this.addStatusMessage('Pipeline: Validating request', 'request');
    await this.delay(200);
    this.addStatusMessage('Pipeline: Validation passed', 'request');

    // Stage 3: Parallel data fetch - SQL
    this.currentFlow!.status = 'traveling-to-target';
    this.addStatusMessage('Pipeline: Fetching from SQL', 'request');

    // Animate to SQL
    await this.animateDot('api', 'sql', 300);
    this.setNodeStatus('sql', 'processing');
    await this.delay(150);
    this.addStatusMessage('Pipeline: SQL query 1/3', 'request');
    await this.delay(100);
    this.addStatusMessage('Pipeline: SQL query 2/3', 'request');
    await this.delay(100);
    this.addStatusMessage('Pipeline: SQL query 3/3', 'request');
    await this.delay(100);
    this.setNodeStatus('sql', 'success');

    // Fire SQL telemetry
    this.animateSqlTelemetry();

    // Return from SQL
    this.requestDotClass = 'dot-success';
    await this.animateDot('sql', 'api', 250);
    this.setNodeStatus('sql', 'idle');

    // Stage 4: Fetch from Redis
    this.addStatusMessage('Pipeline: Fetching from Redis', 'request');
    this.requestDotClass = '';
    await this.animateDot('api', 'redis', 300);
    this.setNodeStatus('redis', 'processing');
    await this.delay(100);
    this.addStatusMessage('Pipeline: Redis SET operation', 'request');
    await this.delay(80);
    this.addStatusMessage('Pipeline: Redis GET operation', 'request');
    await this.delay(80);
    this.addStatusMessage('Pipeline: Redis SET operation', 'request');
    await this.delay(80);
    this.addStatusMessage('Pipeline: Redis GET operation', 'request');
    this.setNodeStatus('redis', 'success');

    // Fire Redis telemetry
    this.animateRedisTelemetry();

    // Return from Redis
    this.requestDotClass = 'dot-success';
    await this.animateDot('redis', 'api', 250);
    this.setNodeStatus('redis', 'idle');

    // Stage 5: Processing with retries
    this.setNodeStatus('api', 'processing');
    this.addStatusMessage('Pipeline: Processing data', 'request');
    await this.delay(200);
    this.addStatusMessage('Pipeline: Transform & enrich', 'request');
    await this.delay(150);

    // Simulate potential retry (40% chance visual)
    if (Math.random() < 0.4) {
      this.addStatusMessage('Pipeline: Transient failure', 'request');
      this.setNodeStatus('api', 'error');
      await this.delay(200);
      this.addStatusMessage('Pipeline: Retry with backoff', 'request');
      await this.delay(300);
      this.setNodeStatus('api', 'processing');
      this.addStatusMessage('Pipeline: Retry succeeded', 'request');
    }

    // Stage 6: Distributed transaction simulation (Saga pattern)
    // Service instances depend on selected scenario:
    // - simple-saga: 4 services with 1 instance each
    // - multi-replica-saga: 4 services with 2 instances each
    if (this.selectedPipelineScenario === 'multi-replica-saga') {
      // Multi-replica: 2 instances per service
      this.addStatusMessage('Saga: order-service (order-001)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: order-service (order-002)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: inventory-service (inventory-001)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: inventory-service (inventory-002)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: payment-service (payment-001)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: payment-service (payment-002)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: notification-service (notification-001)', 'request');
      await this.delay(75);
      this.addStatusMessage('Saga: notification-service (notification-002)', 'request');
      await this.delay(75);
    } else {
      // Simple saga: 1 instance per service
      this.addStatusMessage('Saga: order-service (order-001)', 'request');
      await this.delay(100);
      this.addStatusMessage('Saga: inventory-service (inventory-001)', 'request');
      await this.delay(100);
      this.addStatusMessage('Saga: payment-service (payment-001)', 'request');
      await this.delay(100);
      this.addStatusMessage('Saga: notification-service (notification-001)', 'request');
      await this.delay(100);
    }

    // Stage 7: Finalization
    this.addStatusMessage('Pipeline: Finalizing', 'request');
    await this.delay(100);

    // Send main API telemetry
    this.animateTelemetry('Pipeline');

    // Return to client
    this.currentFlow!.status = 'returning';
    this.addStatusMessage('Pipeline: Returning response', 'request');
    await this.animateDot('api', 'client', 300);
    this.setNodeStatus('api', 'idle');
    this.setNodeStatus('client', 'success');
    this.addStatusMessage('Pipeline: Complete!', 'request');
    await this.delay(300);

    this.currentFlow!.status = 'success';
    this.setNodeStatus('client', 'idle');
    this.requestDotVisible = false;

    this.currentFlow = null;
    this.isAnimating = false;
  }

  private async animateTelemetry(source: string = 'API'): Promise<void> {
    this.telemetryDotProgress = 0;
    this.telemetryDotVisible = true;
    this.addStatusMessage(`${source} sending telemetry to OTel`, 'telemetry');
    this.cdr.detectChanges();

    const steps = 20;
    const stepDuration = 400 / steps;

    for (let i = 0; i <= steps; i++) {
      this.telemetryDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.setNodeStatus('otel', 'processing');
    this.telemetryDotVisible = false;
    this.addStatusMessage(`OTel processing ${source} traces`, 'telemetry');

    // Fire dots to Immersive APM and Others in parallel
    this.animateToImmersiveApm(source);
    this.animateToOthers(source);

    await this.delay(400);
    this.setNodeStatus('otel', 'idle');
    this.cdr.detectChanges();
  }

  private async animateToImmersiveApm(source: string = 'API'): Promise<void> {
    this.immersiveApmDotProgress = 0;
    this.immersiveApmDotVisible = true;
    this.addStatusMessage(`Exporting ${source} traces to DeepCube`, 'telemetry');
    this.cdr.detectChanges();

    const steps = 20;
    const stepDuration = 300 / steps;

    for (let i = 0; i <= steps; i++) {
      this.immersiveApmDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.setNodeStatus('immersive-apm', 'processing');
    this.addStatusMessage(`DeepCube received ${source} traces`, 'telemetry');
    await this.delay(300);
    this.immersiveApmDotVisible = false;
    this.setNodeStatus('immersive-apm', 'idle');
    this.cdr.detectChanges();

    // Continue to IAPM client tools (desktop and web in parallel)
    this.animateToIapmClient('desktop');
    this.animateToIapmClient('web');
  }

  private async animateToOthers(source: string = 'API'): Promise<void> {
    this.othersDotProgress = 0;
    this.othersDotVisible = true;
    this.addStatusMessage(`Exporting ${source} traces to Others`, 'telemetry');
    this.cdr.detectChanges();

    const steps = 20;
    const stepDuration = 300 / steps;

    for (let i = 0; i <= steps; i++) {
      this.othersDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.setNodeStatus('others', 'processing');
    this.addStatusMessage(`Others received ${source} traces`, 'telemetry');
    await this.delay(300);
    this.othersDotVisible = false;
    this.setNodeStatus('others', 'idle');
    this.cdr.detectChanges();

    // Continue to client tools
    this.animateToOthersClient();
  }

  private async animateToIapmClient(target: 'desktop' | 'web' = 'desktop'): Promise<void> {
    const steps = 25;
    const stepDuration = 400 / steps;

    if (target === 'desktop') {
      this.iapmDesktopDotProgress = 0;
      this.iapmDesktopDotVisible = true;
      this.cdr.detectChanges();

      for (let i = 0; i <= steps; i++) {
        this.iapmDesktopDotProgress = i / steps;
        this.cdr.detectChanges();
        await this.delay(stepDuration);
      }

      this.iapmDesktopDotVisible = false;
    } else {
      this.iapmWebDotProgress = 0;
      this.iapmWebDotVisible = true;
      this.cdr.detectChanges();

      for (let i = 0; i <= steps; i++) {
        this.iapmWebDotProgress = i / steps;
        this.cdr.detectChanges();
        await this.delay(stepDuration);
      }

      this.iapmWebDotVisible = false;
    }
    this.cdr.detectChanges();
  }

  private async animateToOthersClient(): Promise<void> {
    this.othersClientDotProgress = 0;
    this.othersClientDotVisible = true;
    this.cdr.detectChanges();

    const steps = 30;
    const stepDuration = 500 / steps;

    for (let i = 0; i <= steps; i++) {
      this.othersClientDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.othersClientDotVisible = false;
    this.cdr.detectChanges();
  }

  private setNodeStatus(nodeId: string, status: 'idle' | 'processing' | 'success' | 'error'): void {
    const node = this.nodes.find(n => n.id === nodeId);
    if (node) {
      node.status = status;
    }
  }

  private async animateDot(fromNodeId: string, toNodeId: string, duration: number, endProgress: number = 1): Promise<void> {
    const steps = 30;
    const stepDuration = duration / steps;

    for (let i = 0; i <= steps * endProgress; i++) {
      this.requestDotPosition = {
        fromNode: fromNodeId,
        toNode: toNodeId,
        progress: i / steps
      };
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }
  }

  private delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  isBreakableConnection(connection: Connection): boolean {
    return connection.resource !== 'api' && connection.resource !== 'otel';
  }

  getNodeClass(node: NetworkNode): string {
    const classes = ['node', `node-${node.type}`];
    if (node.status !== 'idle') {
      classes.push(`status-${node.status}`);
    }
    return classes.join(' ');
  }

  getConnectionClass(connection: Connection): string {
    const classes = ['connection', `connection-${connection.status}`];
    if (this.isBreakableConnection(connection)) {
      classes.push('breakable');
    }
    return classes.join(' ');
  }

  // Get SVG position for request dot
  getRequestDotSvgPosition(): { x: number; y: number } {
    const { fromNode, toNode, progress } = this.requestDotPosition;

    const from = NODE_POSITIONS[fromNode] || { x: 80, y: 150 };
    const to = NODE_POSITIONS[toNode] || { x: 400, y: 150 };

    return {
      x: from.x + (to.x - from.x) * progress,
      y: from.y + (to.y - from.y) * progress
    };
  }

  // Get SVG position for telemetry dot (follows line from API to OTel)
  getTelemetryDotSvgPosition(): { x: number; y: number } {
    // Line: x1="690" y1="260" x2="690" y2="370"
    const from = { x: 690, y: 260 };
    const to = { x: 690, y: 370 };

    return {
      x: from.x + (to.x - from.x) * this.telemetryDotProgress,
      y: from.y + (to.y - from.y) * this.telemetryDotProgress
    };
  }

  // Get SVG position for Immersive APM dot (OTel to IAPM Cloud, 45° down-left)
  getImmersiveApmDotSvgPosition(): { x: number; y: number } {
    // Line: x1="660" y1="460" x2="610" y2="465"
    const from = { x: 660, y: 460 };
    const to = { x: 610, y: 465 };

    return {
      x: from.x + (to.x - from.x) * this.immersiveApmDotProgress,
      y: from.y + (to.y - from.y) * this.immersiveApmDotProgress
    };
  }

  // Get SVG position for Others dot (OTel to Other Plumbing, 45° down-right)
  getOthersDotSvgPosition(): { x: number; y: number } {
    // Line: x1="720" y1="460" x2="770" y2="465"
    const from = { x: 720, y: 460 };
    const to = { x: 770, y: 465 };

    return {
      x: from.x + (to.x - from.x) * this.othersDotProgress,
      y: from.y + (to.y - from.y) * this.othersDotProgress
    };
  }

  // Get SVG position for IAPM Desktop dot (cubic bezier, plugs horizontally into box)
  getIapmDesktopDotSvgPosition(): { x: number; y: number } {
    const start = { x: 540, y: 490 };
    const cp1 = { x: 520, y: 310 };
    const cp2 = { x: 510, y: 130 };
    const end = { x: 470, y: 130 };
    return this.getCubicBezierPosition(start, cp1, cp2, end, this.iapmDesktopDotProgress);
  }

  // Get SVG position for IAPM Web dot (cubic bezier, plugs horizontally into box)
  getIapmWebDotSvgPosition(): { x: number; y: number } {
    const start = { x: 540, y: 505 };
    const cp1 = { x: 520, y: 370 };
    const cp2 = { x: 510, y: 230 };
    const end = { x: 470, y: 230 };
    return this.getCubicBezierPosition(start, cp1, cp2, end, this.iapmWebDotProgress);
  }

  // Get SVG position for Others client dot (smooth two-segment cubic: sweeps below IAPM Cloud then up)
  getOthersClientDotSvgPosition(): { x: number; y: number } {
    const endY = this.getOtherToolsY();
    const t = this.othersClientDotProgress;

    if (t < 0.5) {
      // Segment 1: bottom of Other Plumbing → sweep below IAPM Cloud
      const segT = t / 0.5;
      const start = { x: 770, y: 555 };
      const cp1 = { x: 830, y: 570 };
      const cp2 = { x: 630, y: 575 };
      const end = { x: 500, y: 570 };
      return this.getCubicBezierPosition(start, cp1, cp2, end, segT);
    } else {
      // Segment 2: smooth continuation up to Other Tools center (reflected control point from seg 1)
      const segT = (t - 0.5) / 0.5;
      const centerY = endY + 35; // Center of Other Tools box (height=70)
      const start = { x: 500, y: 570 };
      const cp1 = { x: 370, y: 565 };  // Reflected: (500*2-630, 570*2-575)
      const cp2 = { x: 530, y: centerY };
      const end = { x: 470, y: centerY };
      return this.getCubicBezierPosition(start, cp1, cp2, end, segT);
    }
  }

  // Calculate position along a quadratic bezier curve
  // P(t) = (1-t)²P0 + 2(1-t)tP1 + t²P2
  private getQuadraticBezierPosition(
    start: { x: number; y: number },
    control: { x: number; y: number },
    end: { x: number; y: number },
    t: number
  ): { x: number; y: number } {
    const oneMinusT = 1 - t;
    return {
      x: oneMinusT * oneMinusT * start.x + 2 * oneMinusT * t * control.x + t * t * end.x,
      y: oneMinusT * oneMinusT * start.y + 2 * oneMinusT * t * control.y + t * t * end.y
    };
  }

  // Calculate position along a cubic bezier curve
  // P(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3
  private getCubicBezierPosition(
    start: { x: number; y: number },
    cp1: { x: number; y: number },
    cp2: { x: number; y: number },
    end: { x: number; y: number },
    t: number
  ): { x: number; y: number } {
    const oneMinusT = 1 - t;
    const oneMinusT2 = oneMinusT * oneMinusT;
    const oneMinusT3 = oneMinusT2 * oneMinusT;
    const t2 = t * t;
    const t3 = t2 * t;
    return {
      x: oneMinusT3 * start.x + 3 * oneMinusT2 * t * cp1.x + 3 * oneMinusT * t2 * cp2.x + t3 * end.x,
      y: oneMinusT3 * start.y + 3 * oneMinusT2 * t * cp1.y + 3 * oneMinusT * t2 * cp2.y + t3 * end.y
    };
  }

  // Get SVG position for SQL telemetry dot (along curved path)
  getSqlTelemetryDotSvgPosition(): { x: number; y: number } {
    // Path: M 900 180 Q 800 330 730 380
    const start = { x: 900, y: 180 };
    const control = { x: 800, y: 330 };
    const end = { x: 730, y: 380 };
    return this.getQuadraticBezierPosition(start, control, end, this.sqlTelemetryDotProgress);
  }

  // Get SVG position for Redis telemetry dot (along curved path)
  getRedisTelemetryDotSvgPosition(): { x: number; y: number } {
    // Path: M 900 290 Q 810 350 730 390
    const start = { x: 900, y: 290 };
    const control = { x: 810, y: 350 };
    const end = { x: 730, y: 390 };
    return this.getQuadraticBezierPosition(start, control, end, this.redisTelemetryDotProgress);
  }

  // Animate telemetry from SQL to OpenTelemetry
  private async animateSqlTelemetry(): Promise<void> {
    if (!this.showOptionalCapabilities) return;

    this.sqlTelemetryDotProgress = 0;
    this.sqlTelemetryDotVisible = true;
    this.addStatusMessage('SQL sending telemetry to OTel', 'telemetry');
    this.cdr.detectChanges();

    const steps = 20;
    const stepDuration = 350 / steps;

    for (let i = 0; i <= steps; i++) {
      this.sqlTelemetryDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.sqlTelemetryDotVisible = false;
    this.addStatusMessage('OTel processing SQL traces', 'telemetry');

    // When SQL telemetry reaches OpenTelemetry, forward to APM destinations
    this.setNodeStatus('otel', 'processing');
    this.animateToImmersiveApm('SQL');
    this.animateToOthers('SQL');
    await this.delay(400);
    this.setNodeStatus('otel', 'idle');
    this.cdr.detectChanges();
  }

  // Animate telemetry from Redis to OpenTelemetry
  private async animateRedisTelemetry(): Promise<void> {
    if (!this.showOptionalCapabilities) return;

    this.redisTelemetryDotProgress = 0;
    this.redisTelemetryDotVisible = true;
    this.addStatusMessage('Redis sending telemetry to OTel', 'telemetry');
    this.cdr.detectChanges();

    const steps = 20;
    const stepDuration = 350 / steps;

    for (let i = 0; i <= steps; i++) {
      this.redisTelemetryDotProgress = i / steps;
      this.cdr.detectChanges();
      await this.delay(stepDuration);
    }

    this.redisTelemetryDotVisible = false;
    this.addStatusMessage('OTel processing Redis traces', 'telemetry');

    // When Redis telemetry reaches OpenTelemetry, forward to APM destinations
    this.setNodeStatus('otel', 'processing');
    this.animateToImmersiveApm('Redis');
    this.animateToOthers('Redis');
    await this.delay(400);
    this.setNodeStatus('otel', 'idle');
    this.cdr.detectChanges();
  }

  // Toggle fullscreen mode
  toggleFullscreen(): void {
    this.isFullscreen = !this.isFullscreen;

    if (this.isFullscreen) {
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = '';
    }
  }
}
