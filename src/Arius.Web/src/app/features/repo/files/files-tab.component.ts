import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { toArray } from 'rxjs/operators';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../../core/api/api.service';
import { RealtimeService } from '../../../core/api/realtime.service';
import { DrawerStore } from '../../../core/state/drawer.store';
import { SnapshotStore } from '../../../core/state/snapshot.store';
import { EntryDto } from '../../../core/api/api-models';
import { StateRingComponent } from '../../../shared/state-ring/state-ring.component';
import { StateLegendComponent } from '../../../shared/state-legend/state-legend.component';
import { formatBytes } from '../../../shared/format';

interface TreeRow { path: string; name: string; depth: number; expandable: boolean; expanded: boolean; }

/** Natural, case-insensitive name sort so "file2" sorts before "file10". */
const byName = (a: EntryDto, b: EntryDto) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });

/** Files tab: snapshot time-travel bar + Explorer (folder tree + file detail with state rings + collect). */
@Component({
  selector: 'arius-files-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, StateRingComponent, StateLegendComponent],
  template: `
    <!-- Collected action bar -->
    @if (collectedCount() > 0) {
      <div data-testid="collected-bar" style="margin-top:14px;display:flex;align-items:center;gap:14px;background:#eff6ff;border:1px solid #dbeafe;border-radius:11px;padding:11px 16px">
        <i class="ki-filled ki-check-square" style="color:#3b82f6;font-size:18px"></i>
        <span style="font-size:13.5px;color:#1d4ed8"><b>{{ collectedCount() }}</b> files collected · {{ formatBytes(collectedBytes()) }}</span>
        <div style="margin-left:auto;display:flex;gap:8px">
          <button class="ar-btn-outline" (click)="clearCollected()">Clear</button>
          <button class="ar-btn-primary" data-testid="restore-collected" (click)="restoreCollected()"><i class="ki-filled ki-cloud-download"></i>Restore collected</button>
        </div>
      </div>
    }

    <!-- Explorer -->
    <div class="ar-card" style="margin-top:14px;display:flex;flex-direction:column;height:calc(100vh - 380px);min-height:420px;overflow:hidden">
      <!-- Toolbar -->
      <div class="flex items-center gap-2" style="padding:11px 14px;border-bottom:1px solid #f4f4f5">
        <button class="ar-icon-btn" [disabled]="!selectedFolder()" (click)="goUp()"><i class="ki-filled ki-up"></i></button>
        <div class="ar-mono" style="flex:1;background:#f7f7f8;border-radius:8px;padding:7px 11px;font-size:12.5px;color:#52525b">/{{ container() }}{{ selectedFolder() ? '/' + selectedFolder() : '' }}</div>
        <div class="flex items-center gap-2" style="width:240px;background:#fff;border:1px solid #e4e4e7;border-radius:8px;padding:6px 10px">
          <i class="ki-filled ki-magnifier" style="font-size:14px;color:#a1a1aa"></i>
          <input [ngModel]="fileFilter()" (ngModelChange)="onFilter($event)" placeholder="Filter files in this folder…"
                 data-testid="file-filter" class="grow bg-transparent outline-none text-[13px]" />
        </div>
      </div>

      <!-- Body -->
      <div style="flex:1;display:flex;min-height:0">
        <!-- Tree -->
        <div style="width:288px;border-right:1px solid #f4f4f5;overflow-y:auto;padding:8px 0">
          <button class="ar-tree-row" [class.sel]="selectedFolder() === ''" (click)="selectFolder('')"
                  style="display:flex;align-items:center;gap:7px;width:100%;padding:6px 8px;font-size:13px">
            <i class="ki-filled ki-data" style="color:#3b82f6;font-size:15px"></i>
            <span style="font-weight:600;color:#27272a">{{ alias() }}</span>
          </button>
          @for (row of treeRows(); track row.path) {
            <button class="ar-tree-row" data-testid="tree-node" [class.sel]="selectedFolder() === row.path" (click)="onTreeClick(row)"
                    style="display:flex;align-items:center;gap:5px;width:100%;font-size:13px"
                    [style.padding-left.px]="8 + row.depth * 18" [style.padding-top.px]="6" [style.padding-bottom.px]="6" [style.padding-right.px]="8">
              <i class="ki-filled ki-down" style="font-size:11px;color:#a1a1aa;transition:transform .12s"
                 [style.transform]="row.expanded ? 'rotate(0)' : 'rotate(-90deg)'" [style.visibility]="row.expandable ? 'visible' : 'hidden'"></i>
              <i class="ki-filled ki-folder" style="color:#fbbf24;font-size:15px"></i>
              <span [title]="row.name" style="color:#3f3f46;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{{ row.name }}</span>
            </button>
          }
        </div>

        <!-- Detail (files only) -->
        <div style="flex:1;display:flex;flex-direction:column;min-width:0">
          <div style="display:grid;grid-template-columns:34px 2fr 1.2fr .7fr .7fr .9fr;align-items:center;padding:9px 14px;border-bottom:1px solid #f4f4f5;font-size:11px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">
            <div></div><div>Name</div><div>State</div><div>Size</div><div>Tier</div><div>Modified</div>
          </div>
          <div style="flex:1;overflow-y:auto">
            @if (filesError()) {
              <div style="padding:24px 14px;color:#b45309;font-size:13px">
                <i class="ki-filled ki-information-2"></i> Could not load entries: {{ filesError() }}
              </div>
            } @else {
              @for (f of shownFiles(); track f.relativePath) {
                <div class="ar-file-row" data-testid="file-row" (click)="toggleCollect(f)"
                     [class.hl]="highlighted() === f.relativePath"
                     style="display:grid;grid-template-columns:34px 2fr 1.2fr .7fr .7fr .9fr;align-items:center;font-size:13px"
                     [style.height.px]="46" [style.background]="rowBg(f)">
                  <div style="display:flex;justify-content:center">
                    <span class="ar-check" [class.on]="collected().has(f.relativePath)"><i class="ki-filled ki-check"></i></span>
                  </div>
                  <div class="flex items-center gap-2.5" style="min-width:0;padding-right:8px">
                    <i class="ki-outline ki-document" style="color:#a1a1aa;font-size:16px"></i>
                    <span data-testid="file-name" [title]="f.name" style="color:#27272a;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{{ f.name }}</span>
                  </div>
                  <div class="flex items-center gap-2">
                    <arius-state-ring [state]="f.state" [size]="19" />
                    <span style="font-size:12px" [style.color]="stateMeta(f).color">{{ stateMeta(f).label }}</span>
                  </div>
                  <div style="color:#52525b">{{ formatBytes(f.originalSize) }}</div>
                  <div><span style="font-size:12px;font-weight:600" [style.color]="tierMeta(f).color">{{ tierMeta(f).label }}</span></div>
                  <div style="color:#a1a1aa;font-size:12.5px">{{ f.modified ? (f.modified | date:'dd MMM yyyy') : '—' }}</div>
                </div>
              } @empty {
                <div style="padding:24px 14px;color:#a1a1aa;font-size:13px">
                  {{ filesLoading() ? 'Loading…' : 'No files in this folder.' }}
                </div>
              }
            }
          </div>

          <!-- Footer -->
          <div class="flex items-center justify-between" style="padding:9px 14px;border-top:1px solid #f4f4f5;font-size:12.5px;color:#71717a">
            <span><b>{{ shownFiles().length }}</b> files · {{ formatBytes(shownBytes()) }} shown</span>
            <arius-state-legend />
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .ar-icon-btn { width:32px;height:32px;border-radius:8px;border:1px solid #e4e4e7;color:#71717a;display:flex;align-items:center;justify-content:center }
    .ar-icon-btn:disabled { opacity:.4 }
    .ar-tree-row.sel { background:#eff6ff }
    .ar-check { width:18px;height:18px;border-radius:5px;border:1.5px solid #d4d4d8;display:flex;align-items:center;justify-content:center;color:transparent;font-size:11px }
    .ar-check.on { background:#3b82f6;border-color:#3b82f6;color:#fff }
    .ar-file-row.hl { box-shadow:inset 3px 0 0 #f59e0b }
  `],
})
export class FilesTabComponent {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly drawer = inject(DrawerStore);
  private readonly snap = inject(SnapshotStore);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly repoId = input.required<string>();
  /** Optional full relativePath of a file to reveal (set by global search via the `path` query param). */
  readonly path = input<string>();

  protected readonly selectedFolder = signal('');
  protected readonly fileFilter = signal('');
  protected readonly collected = signal<Map<string, number>>(new Map());
  /** relativePath of the file to highlight after a reveal-from-search navigation. */
  protected readonly highlighted = signal<string | null>(null);

  protected readonly expanded = signal<Set<string>>(new Set());
  protected readonly dirCache = signal<Map<string, EntryDto[]>>(new Map());
  protected readonly files = signal<EntryDto[]>([]);
  protected readonly filesLoading = signal(false);
  protected readonly filesError = signal<string | null>(null);

  protected readonly alias = signal('Repository');
  protected readonly container = signal('');

  protected readonly formatBytes = formatBytes;

  private filterTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    // Repo metadata for the tree header.
    effect(() => {
      const id = +this.repoId();
      untracked(() => this.api.getRepository(id).subscribe(r => { this.alias.set(r.alias); this.container.set(r.container); }));
    });
    // Reload the explorer whenever the repo, the selected snapshot (shared, from the bar),
    // or the file-to-reveal (global-search `path` query param) changes.
    effect(() => {
      this.repoId();
      this.snap.version();
      const path = this.path();
      untracked(() => void this.resetAndReveal(path));
    });
  }

  private async resetAndReveal(path: string | undefined): Promise<void> {
    this.selectedFolder.set('');
    this.fileFilter.set('');
    this.expanded.set(new Set());
    this.dirCache.set(new Map());
    this.highlighted.set(null);
    await this.loadChildren('');
    if (path) await this.revealPath(path);
    else await this.loadFiles('');
  }

  /** Expand the tree down to the file's folder, open that folder, and highlight the file row. */
  private async revealPath(filePath: string): Promise<void> {
    const folder = filePath.includes('/') ? filePath.slice(0, filePath.lastIndexOf('/')) : '';
    if (folder) {
      let acc = '';
      for (const seg of folder.split('/')) {
        await this.loadChildren(acc);          // load the parent's dirs so this segment's row exists
        acc = acc ? `${acc}/${seg}` : seg;
        this.expanded.update(s => new Set(s).add(acc));
      }
      await this.loadChildren(folder);          // load the target folder's subfolders (for its chevron)
    }
    this.selectedFolder.set(folder);
    await this.loadFiles(folder);
    this.highlighted.set(filePath);
    this.scrollHighlightedIntoView();
  }

  private scrollHighlightedIntoView(): void {
    setTimeout(() => {
      const el = this.host.nativeElement.querySelector('.ar-file-row.hl') as HTMLElement | null;
      el?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }, 60);
  }

  // ── Tree ────────────────────────────────────────────────────────────────
  protected readonly treeRows = computed<TreeRow[]>(() => {
    const cache = this.dirCache();
    const exp = this.expanded();
    const rows: TreeRow[] = [];
    const walk = (path: string, depth: number) => {
      for (const dir of cache.get(path) ?? []) {
        const childPath = dir.relativePath;
        const expanded = exp.has(childPath);
        rows.push({ path: childPath, name: dir.name, depth, expandable: true, expanded });
        if (expanded) walk(childPath, depth + 1);
      }
    };
    walk('', 0);
    return rows;
  });

  protected onTreeClick(row: TreeRow): void {
    this.selectFolder(row.path);
    const exp = new Set(this.expanded());
    if (exp.has(row.path)) exp.delete(row.path); else { exp.add(row.path); void this.loadChildren(row.path); }
    this.expanded.set(exp);
  }

  protected selectFolder(path: string): void {
    this.highlighted.set(null);
    this.selectedFolder.set(path);
    void this.loadFiles(path);
  }

  protected goUp(): void {
    const cur = this.selectedFolder();
    if (!cur) return;
    const parent = cur.includes('/') ? cur.slice(0, cur.lastIndexOf('/')) : '';
    this.selectFolder(parent);
  }

  private async loadChildren(path: string): Promise<void> {
    if (this.dirCache().has(path)) return;
    try {
      const entries = await this.fetch(path, null);
      const dirs = entries.filter(e => e.kind === 'dir').sort(byName);
      const next = new Map(this.dirCache());
      next.set(path, dirs);
      this.dirCache.set(next);
    } catch {
      const next = new Map(this.dirCache());
      next.set(path, []);
      this.dirCache.set(next);
    }
  }

  private async loadFiles(path: string): Promise<void> {
    this.filesLoading.set(true);
    this.filesError.set(null);
    this.files.set([]);
    try {
      const entries = await this.fetch(path, this.fileFilter() || null);
      this.files.set(entries.filter(e => e.kind === 'file').sort(byName));
    } catch (err: unknown) {
      this.filesError.set(err instanceof Error ? err.message : String(err));
    } finally {
      this.filesLoading.set(false);
    }
  }

  private fetch(prefix: string, filter: string | null): Promise<EntryDto[]> {
    return firstValueFrom(
      this.realtime.listEntries(+this.repoId(), {
        version: this.snap.version(),
        prefix: prefix || null,
        filter,
        // Only overlay the local file system on the live working state; a historical snapshot
        // shows the archive's contents as they were, without the current local folder.
        includeLocal: this.snap.version() === null,
      }).pipe(toArray()),
    );
  }

  // ── Filter ────────────────────────────────────────────────────────────────
  protected onFilter(value: string): void {
    this.highlighted.set(null);
    this.fileFilter.set(value);
    clearTimeout(this.filterTimer);
    this.filterTimer = setTimeout(() => this.loadFiles(this.selectedFolder()), 250);
  }

  /** Row background: revealed file wins over a collected row. */
  protected rowBg(f: EntryDto): string {
    if (this.highlighted() === f.relativePath) return '#fff7ed';
    if (this.collected().has(f.relativePath)) return '#f7f9ff';
    return '';
  }

  protected readonly shownFiles = computed(() => this.files());
  protected readonly shownBytes = computed(() => this.files().reduce((sum, f) => sum + (f.originalSize ?? 0), 0));

  // ── Collect ────────────────────────────────────────────────────────────────
  protected toggleCollect(f: EntryDto): void {
    const next = new Map(this.collected());
    if (next.has(f.relativePath)) next.delete(f.relativePath);
    else next.set(f.relativePath, f.originalSize ?? 0);
    this.collected.set(next);
  }
  protected clearCollected(): void { this.collected.set(new Map()); }
  protected restoreCollected(): void {
    this.drawer.openRestore(+this.repoId(), this.snap.version(), [...this.collected().keys()]);
  }
  protected readonly collectedCount = computed(() => this.collected().size);
  protected readonly collectedBytes = computed(() => [...this.collected().values()].reduce((a, b) => a + b, 0));

  // ── State / tier labels (from flags — what real data provides) ──────────────
  protected stateMeta(f: EntryDto): { label: string; color: string } {
    const s = f.stateFlags;
    if (s.repositoryRehydrating) return { label: 'Rehydrating', color: '#6d28d9' };
    if (s.repositoryArchived) return { label: 'Archive tier', color: '#6d28d9' };
    if (s.repository && s.localBinary) return { label: 'In sync', color: '#15803d' };
    if (s.repository && s.localPointer) return { label: 'Pointer only', color: '#0369a1' };
    if (s.repository) return { label: 'In repository', color: '#0369a1' };
    if (s.localBinary) return { label: 'Not archived', color: '#b45309' };
    return { label: 'Mixed', color: '#52525b' };
  }
  protected tierMeta(f: EntryDto): { label: string; color: string } {
    const s = f.stateFlags;
    if (s.repositoryArchived || s.repositoryRehydrating) return { label: 'Archive', color: '#8b5cf6' };
    if (s.repositoryHydrated) return { label: 'Online', color: '#3b82f6' };
    return { label: '—', color: '#a1a1aa' };
  }
}
