import { ChangeDetectorRef, Component, EventEmitter, OnInit, ChangeDetectionStrategy } from '@angular/core';
import { SandboxService } from '../../services/sandbox.service';
import { FlowService, SqlScenario, RedisScenario, PipelineScenario } from '../../services/flow.service';
import { catchError, first, forkJoin, merge, of, switchMap, tap } from 'rxjs';
import { FailureService } from '../../services/failure.service';
import { ActivatedRoute, Router } from '@angular/router';
import { environment } from '../../../environments/environment';
import { FlowRequest } from '../network-diagram/network-diagram.component';

@Component({
    selector: 'app-sandbox',
    templateUrl: './sandbox.component.html',
    styleUrl: './sandbox.component.scss',
    changeDetection: ChangeDetectionStrategy.Eager,
    standalone: false
})
export class SandboxComponent implements OnInit {
  showMoreHelp = false;
  sandboxId?: string;

  generateSandboxEvent = new EventEmitter<boolean>;
  output: string[] = [];

  isRunning = 0;
  newData = 0;

  resources:{ [id: string] : boolean } = {
    'sql': false,
    'redis': false
  };
  discordUrl: string | undefined;
  gitHubUrl: string | undefined;
  requiresAccountToVisualize = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private sandboxService: SandboxService,
    private flowService: FlowService,
    private failureService: FailureService,
    private cdr: ChangeDetectorRef
  ) {
  }

  ngOnInit(): void {
    this.sandboxId = (this.route.snapshot.params as any).sandboxId;
    this.gitHubUrl = environment.gitHubUrl;
    this.discordUrl = environment.discordUrl;
    this.requiresAccountToVisualize = environment.requiresAccountToVisualize;

    merge(this.generateSandboxEvent)
      .pipe(
        switchMap(() => this.sandboxService.get()),
        catchError((e) => {
          //console.log(e)
          return of({});
        }
        ))
      .subscribe((response) => {

        const sandboxId = response.value;

        if (sandboxId) {
          window.location.href = `/sandbox/${sandboxId}`;
        }
      })


    if (!this.sandboxId) {

      this.generateSandboxEvent.next(true);

    }
    else {
      //sandbox ready to use
      this.terminalLog('Sandbox ready');
    }

    const resourceKeys = this.getResourceKeys();
    const callbacks = resourceKeys.map(r => this.failureService.status(r, this.sandboxId!));

    forkJoin(callbacks).pipe(first()).subscribe(results => {
      results.forEach((r, i) => {
        this.resources[resourceKeys[i]] = r.value;
      });
    });
  }

  getResourceKeys() : string[]
  {
    return Object.keys(this.resources);
  }

  terminalLog(message: string) {
    this.output.push(`> ${message}`);
    this.cdr.detectChanges();
  }

  regenerateSandbox() {
    this.generateSandboxEvent.next(true);
  }

  copySandboxIdToClipboard()
  {
    if (!this.sandboxId)
    {
      return;
    }

    // Copy the text inside the text field
    navigator.clipboard.writeText(this.sandboxId!);
  }

  clearTerminal()
  {
    this.output = [];
  }

  toggle(resource: string) {

    this.isRunning++;

    if (this.resources[resource]) //open?
    {
      this.terminalLog(`Fixing (ejecting the error from) ${resource}. Please wait... (if this fails, there is an issue with sample application and/or its deployment)`)

      this.failureService.eject(resource, this.sandboxId!)
      .pipe(catchError(e => {
        this.terminalLog(`[FAILURE]: ${resource} could not break: ${JSON.stringify(e)}`)
        return of({failed: true});
      }),first())
      .subscribe((response) => {
        this.isRunning--;
        this.newData++;

        if (response.failed)
        {
          return;
        }         


        
        this.resources = { ...this.resources, [resource]: false };
        this.terminalLog(`${resource} switched to 'available' (circuit is closed)`);

      });
    }
    else
    {
      this.terminalLog(`Breaking (injecting error into) ${resource}. Please wait... (if this fails, there is an issue with sample application and/or its deployment)`)

      this.failureService.inject(resource, this.sandboxId!)
      .pipe(catchError(e => {
        this.terminalLog(`[FAILURE]: ${resource} could not break: ${JSON.stringify(e)}`)
        return of({failed: true});
      }),first())
      .subscribe((response) => {
        this.isRunning--;
        this.newData++;

        if (response.failed)
        {
          return;
        }         

        this.resources = { ...this.resources, [resource]: true };
        this.terminalLog(`${resource} switched to 'unavailable' (circuit is open)`);
      });
    }

  }

  execute(flowRequest: FlowRequest | string) {
    // Handle both old string format and new FlowRequest format
    const resource = typeof flowRequest === 'string' ? flowRequest : flowRequest.resource;
    const scenario = typeof flowRequest === 'string' ? 'success' : flowRequest.scenario;

    this.terminalLog(`Executing ${resource} request (scenario: ${scenario}). Please wait... (if failure was injected this may take a few seconds)`)

    this.isRunning++;

    switch(resource)
    {
      case 'sql':
        this.flowService.executeSql(this.sandboxId!, scenario as SqlScenario)
        .pipe(
          tap(() => {
            //nothing
          }),
          catchError(e => {
            this.terminalLog(`[FAILURE]: ${resource} request failed to complete successfully: ${JSON.stringify(e)}`)
            return of({failed: true});
          }),
          first())

        .subscribe((response: any) => {
          this.isRunning--;
          this.newData++;

          if (response.failed)
          {
            return;
          }

          this.terminalLog(`[SUCCESS]: ${resource} (${scenario}) completed successfully: ${JSON.stringify(response.value)}`)
        });
        break;
      case 'redis':
        this.flowService.executeRedis(this.sandboxId!, scenario as RedisScenario)
        .pipe(
          tap(() => {
            //nothing
          }),
          catchError(e => {
            this.terminalLog(`[FAILURE]: ${resource} request failed to complete successfully: ${JSON.stringify(e)}`)
            return of({failed: true});
          }),
          first())
        .subscribe((response: any) => {
          this.isRunning--;
          this.newData++;

          if (response.failed)
          {
            return;
          }

          this.terminalLog(`[SUCCESS]: ${resource} (${scenario}) completed successfully: ${JSON.stringify(response.value)}`)

        });
        break;
      case 'pipeline':
        this.flowService.executePipeline(this.sandboxId!, scenario as PipelineScenario)
        .pipe(
          tap(() => {
            //nothing
          }),
          catchError(e => {
            this.terminalLog(`[FAILURE]: ${resource} request failed to complete successfully: ${JSON.stringify(e)}`)
            return of({failed: true});
          }),
          first())
        .subscribe((response: any) => {
          this.isRunning--;
          this.newData++;

          if (response.failed)
          {
            return;
          }

          this.terminalLog(`[SUCCESS]: ${resource} (${scenario}) completed successfully: ${JSON.stringify(response.value)}`)

        });
        break;
        default:
          this.isRunning--;
    }
  }

  visualize() {

    if (!this.sandboxId)
    {
      return;
    }

    environment.visualize(this.sandboxId!);
  }

  
  startSubscription() {
    window.open(environment.subscriptionUrl);
  }

  clone() {
    window.open(environment.gitHubUrl);
  }
}
