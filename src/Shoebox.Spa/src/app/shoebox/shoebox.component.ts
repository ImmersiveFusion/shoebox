import { Component, ElementRef, HostListener, OnInit, ViewChild, inject, signal } from '@angular/core';
import { Subject, debounceTime } from 'rxjs';
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
import { flyRun } from './span-flight';

@Component({
  selector: 'app-shoebox',
  templateUrl: './shoebox.component.html',
  styleUrls: ['./shoebox.component.scss'],
  standalone: false,
})
export class ShoeboxComponent implements OnInit {
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
    });
  }

  private stopFlight: (() => void) | null = null;

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
