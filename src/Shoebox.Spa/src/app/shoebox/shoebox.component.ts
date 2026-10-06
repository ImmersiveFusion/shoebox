import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild, inject, signal } from '@angular/core';
import { Subject, Subscription, debounceTime, interval } from 'rxjs';
import { EXAMPLES, DEFAULT_EXAMPLE, GROUPS, Example, Outcome, outcomeOf } from './examples';
import { OtlpStatus, ParsedTopology, RunResult, ShoeboxService } from './shoebox.service';
import {
  URL_LENGTH_WARNING,
  readDiagramFromUrl,
  readShoeboxFromUrl,
  writeDiagramToUrl,
  writeShoeboxToUrl,
} from './diagram-url';
import { decorate } from './diagram-style';
import { flyRun, markUntaken } from './span-flight';
import {
  DEFAULT_DURATION_SECONDS,
  DEFAULT_INTERVAL_SECONDS,
  DURATIONS,
  EXTEND_SECONDS,
  FiringStatus,
  MAX_INTERVAL_SECONDS,
  MIN_INTERVAL_SECONDS,
  clampInterval,
  describeFiring,
} from './timed-firing';

@Component({
  selector: 'app-shoebox',
  templateUrl: './shoebox.component.html',
  styleUrls: ['./shoebox.component.scss'],
  standalone: false,
})
export class ShoeboxComponent implements OnInit, OnDestroy {
  private readonly service = inject(ShoeboxService);
  private readonly edits = new Subject<void>();

  @ViewChild('render', { static: true }) renderTarget!: ElementRef<HTMLDivElement>;

  /** Rendered as data because Angular would read the braces as interpolation. */
  readonly hexagonExample = 'ext' + '{{' + 'Stripe' + '}}';

  readonly examples = EXAMPLES;

  // From the list, not from whatever order the array happens to be in, so the
  // rows read never, always, sometimes rather than however the file was edited.
  readonly groups = GROUPS.filter(g => EXAMPLES.some(e => e.group === g));

  diagram = DEFAULT_EXAMPLE.diagram;
  selectedExampleId = DEFAULT_EXAMPLE.id;

  readonly topology = signal<ParsedTopology | null>(null);
  readonly result = signal<RunResult | null>(null);
  readonly otlp = signal<OtlpStatus | null>(null);
  readonly renderError = signal<string | null>(null);
  readonly urlTooLong = signal(false);

  runIndex = 1;
  shoeboxId = '';

  // ── Timed firing ──
  //
  // The server owns the timer; this only starts it, shows it and stops it. Off
  // until somebody presses start, and there is no choice without an end.
  readonly durations = DURATIONS;
  readonly minInterval = MIN_INTERVAL_SECONDS;
  readonly maxInterval = MAX_INTERVAL_SECONDS;
  durationSeconds = DEFAULT_DURATION_SECONDS;
  intervalSeconds = DEFAULT_INTERVAL_SECONDS;

  /** The running timer, or null when nothing is firing. */
  readonly firing = signal<FiringStatus | null>(null);
  /** Why the last start, extend or stop was refused, or how the last timer ended. */
  readonly firingNote = signal<string | null>(null);
  private firingPoll: Subscription | null = null;

  /**
   * Which panel, if any, is filling the screen. Both panes are cramped by
   * default: a diagram of a real system does not fit in half a page, and neither
   * does the text that produced it.
   */
  readonly expanded = signal<'editor' | 'viewer' | null>(null);

  /**
   * Mermaid is roughly a quarter of a megabyte, so it is loaded on demand rather
   * than shipped in the initial bundle. The page paints, then the renderer
   * arrives. That matters for a tool whose whole distribution model is somebody
   * clicking a link in a forum thread.
   */
  private mermaid: typeof import('mermaid').default | null = null;

  async ngOnInit(): Promise<void> {
    this.mermaid = (await import('mermaid')).default;
    this.mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      // 'base' rather than 'dark', because dark is still mermaid's grey-on-grey and
      // only themeVariables let the diagram sit in the same world as the rest of
      // the page. The per-node looks are applied separately, in diagram-style.ts.
      theme: 'base',
      themeVariables: {
        darkMode: true,
        background: 'transparent',
        fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
        primaryColor: '#13243a',
        primaryTextColor: '#e8f1f6',
        primaryBorderColor: '#2fd4c4',
        lineColor: '#4d6b83',
        textColor: '#e8f1f6',
        // Edge labels carry the "broken: wrong table" text, so they need to be
        // readable over both the panel and an edge passing underneath.
        edgeLabelBackground: '#0b1622',
        tertiaryColor: '#12202f',
      },
    });

    // A shared link wins over the default, because someone followed it on purpose.
    const fromUrl = await readDiagramFromUrl();
    if (fromUrl) {
      this.diagram = fromUrl;
      this.selectedExampleId = '';
    }

    // A link carries its shoebox as well as its diagram. Opening someone's link
    // puts you in their shoebox, so both of you are firing into the same bucket
    // and one filter on shoebox.id finds the lot.
    this.shoeboxId = readShoeboxFromUrl() ?? '';
    if (this.shoeboxId) {
      writeShoeboxToUrl(this.shoeboxId);
      // A link into a shoebox that is already firing should say so, with its stop
      // button, rather than leave the person wondering where the traces come from.
      this.checkFiring();
    } else {
      this.service.createShoebox().subscribe(r => {
        this.shoeboxId = r.shoeboxId;
        writeShoeboxToUrl(r.shoeboxId);
      });
    }

    this.service.otlpStatus().subscribe(s => this.otlp.set(s));

    // The graph follows you as you type, but not on every keystroke.
    this.edits.pipe(debounceTime(300)).subscribe(() => void this.refresh());
    void this.refresh();
  }

  onDiagramChanged(): void {
    this.selectedExampleId = '';
    this.edits.next();
  }

  loadExample(id: string): void {
    const example = this.examples.find(e => e.id === id);
    if (!example) return;
    this.selectedExampleId = id;
    this.diagram = example.diagram;
    this.runIndex = 1;
    this.result.set(null);
    void this.refresh();
  }

  outcomeOf(example: Example): Outcome {
    return outcomeOf(example);
  }

  examplesIn(group: string): readonly Example[] {
    return this.examples.filter(e => e.group === group);
  }

  descriptionFor(id: string): string {
    return this.examples.find(e => e.id === id)?.description ?? '';
  }

  /** Nothing moves until the user says so. This is the core mechanic. */
  fire(): void {
    this.service.run(this.diagram, this.runIndex, this.shoeboxId).subscribe(result => {
      this.result.set(result);
      this.runIndex += 1;

      // Replay the path the server says the request took. Firing again cancels
      // whatever is still in the air, so two dots are never on the same diagram
      // telling different stories.
      this.stopFlight?.();
      this.stopFlight = flyRun(this.renderTarget.nativeElement, result.hops ?? []);

      // And say what it did not cross. Usually nothing, so usually this only
      // clears the previous run's marks. When it is not nothing, part of the
      // diagram did not run and the picture has to stop implying it did.
      markUntaken(this.renderTarget.nativeElement, result.notTaken ?? []);
    });
  }

  private stopFlight: (() => void) | null = null;

  /** Fires the current diagram on a timer, for the chosen time and no longer. */
  startFiring(): void {
    if (!this.shoeboxId) return;
    this.intervalSeconds = clampInterval(this.intervalSeconds);
    this.firingNote.set(null);
    this.service
      .startFiring(this.diagram, this.shoeboxId, this.durationSeconds, this.intervalSeconds)
      .subscribe({
        next: outcome => this.showFiring(outcome.status),
        error: e => this.firingNote.set(this.errorText(e)),
      });
  }

  stopFiring(): void {
    if (!this.shoeboxId) return;
    this.service.stopFiring(this.shoeboxId).subscribe({
      next: () => this.ended('Stopped'),
      error: e => this.firingNote.set(this.errorText(e)),
    });
  }

  extendFiring(): void {
    if (!this.shoeboxId) return;
    this.service.extendFiring(this.shoeboxId, EXTEND_SECONDS).subscribe({
      next: outcome => {
        this.showFiring(outcome.status);
        this.firingNote.set(outcome.clamped ? 'Extended up to the two hour limit on time left.' : null);
      },
      error: e => this.firingNote.set(this.errorText(e)),
    });
  }

  describeFiring(status: FiringStatus): string {
    return describeFiring(status);
  }

  ngOnDestroy(): void {
    this.firingPoll?.unsubscribe();
  }

  private checkFiring(): void {
    this.service.firingStatus(this.shoeboxId).subscribe({
      next: status => {
        if ('stopsAt' in status && status.firing) {
          this.showFiring(status);
        } else if (this.firing()) {
          this.ended('Finished');
        }
      },
      error: () => undefined,
    });
  }

  private showFiring(status: FiringStatus): void {
    this.firing.set(status);
    // Polled rather than pushed: one small GET every two seconds, only while a
    // timer is running, and it stops the moment the server says it has ended.
    this.firingPoll ??= interval(2000).subscribe(() => this.checkFiring());
  }

  private ended(how: string): void {
    const last = this.firing();
    this.firing.set(null);
    this.firingPoll?.unsubscribe();
    this.firingPoll = null;
    if (last) this.firingNote.set(`${how} after ${last.runs} ${last.runs === 1 ? 'run' : 'runs'}.`);
  }

  private errorText(e: unknown): string {
    const body = (e as { error?: { error?: string } })?.error;
    return body?.error ?? 'The timer could not be changed. Try again in a moment.';
  }

  resetRuns(): void {
    this.runIndex = 1;
    this.result.set(null);
  }

  /**
   * Real full screen when the browser grants it, a fixed overlay when it does
   * not. Nothing here depends on the Fullscreen API succeeding: the `is-expanded`
   * class does the whole job on its own, and `requestFullscreen` only removes the
   * browser chrome on top of that. It needs a real user gesture, so it is
   * expected to be refused when the click was synthetic.
   */
  toggleExpand(which: 'editor' | 'viewer', host: HTMLElement): void {
    if (this.expanded() === which) {
      this.collapse();
      return;
    }
    this.expanded.set(which);
    void host.requestFullscreen?.().catch(() => undefined);
  }

  collapse(): void {
    this.expanded.set(null);
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => undefined);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.expanded()) this.collapse();
  }

  /** Escape inside native full screen is eaten by the browser, so follow its lead. */
  @HostListener('document:fullscreenchange')
  onFullscreenChange(): void {
    if (!document.fullscreenElement && this.expanded()) this.expanded.set(null);
  }

  private async refresh(): Promise<void> {
    await this.render();
    this.service.parse(this.diagram).subscribe(t => this.topology.set(t));
    // A running timer walks whatever it was last sent, so send it the edit. The
    // next run picks it up; nothing restarts.
    if (this.firing() && this.shoeboxId) {
      this.service.updateFiringDiagram(this.diagram, this.shoeboxId).subscribe({ error: () => undefined });
    }
    await writeDiagramToUrl(this.diagram);
    this.urlTooLong.set(window.location.href.length > URL_LENGTH_WARNING);
  }

  private async render(): Promise<void> {
    try {
      if (!this.mermaid) return;
      // The SVG about to be replaced is the one the dot is riding.
      this.stopFlight?.();
      this.stopFlight = null;
      const { svg } = await this.mermaid.render('shoebox-graph', decorate(this.diagram));
      this.renderTarget.nativeElement.innerHTML = svg;
      this.renderError.set(null);
    } catch (error: unknown) {
      // Mermaid throws on a half-typed line. That is normal while editing, so the
      // last good render stays on screen rather than the picture disappearing.
      this.renderError.set(error instanceof Error ? error.message : String(error));
    }
  }
}
