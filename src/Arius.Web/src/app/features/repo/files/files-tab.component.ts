import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { toArray } from 'rxjs/operators';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../../core/api/api.service';
import { RealtimeService } from '../../../core/api/realtime.service';
import { DrawerStore } from '../../../core/state/drawer.store';
import { EntryDto, SnapshotDto } from '../../../core/api/api-models';
import { StateRingComponent } from '../../../shared/state-ring/state-ring.component';
import { StateLegendComponent } from '../../../shared/state-legend/state-legend.component';
import { formatBytes } from '../../../shared/format';

interface TreeRow { path: string; name: string; depth: number; expandable: boolean; expanded: boolean; }

/** Files tab: snapshot time-travel bar + Explorer (folder tree + file detail with state rings + collect). */
@Component({
  selector: 'arius-files-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, StateRingComponent, StateLegendComponent],
  template: `
    <!-- Snapshot / time-travel bar -->
    <div class="ar-card" style="padding:13px 18px;display:flex;align-items:center;gap:18px">
      <div style="position:relative">
        <button class="ar-btn-outline" (click)="pickerOpen.set(!pickerOpen())">
          <i class="ki-filled ki-time"></i>
          <span>Snapshot <b>{{ activeSnapLabel() }}</b></span>
          @if (!viewSnap()) { <span class="ar-pill-green">LATEST</span> }
          <i class="ki-filled ki-down" style="font-size:13px"></i>
        </button>
        @if (pickerOpen()) {
          <div class="ar-snap-menu">
            @for (s of snapshots(); track s.version; let i = $index) {
              <button class="ar-snap-item" (click)="pickSnapshot(s, i)">
                <span style="font-weight:600">v{{ snapshots().length - i }}</span>
                <span style="color:#71717a">{{ s.timestamp | date:'dd MMM yyyy · HH:mm' }}</span>
                <span style="color:#a1a1aa">{{ s.fileCount }} files</span>
                @if (i === 0) { <span class="ar-pill-green">LATEST</span> }
              </button>
            } @empty {
              <div style="padding:12px;color:#a1a1aa;font-size:12.5px">No snapshots</div>
            }
          </div>
        }
      </div>

      <!-- Scrubber -->
      <div style="flex:1;display:flex;align-items:center;gap:10px;height:20px">
        <div style="position:relative;flex:1;height:4px;background:#eef0f3;border-radius:999px">
          @for (s of snapshots(); track s.version; let i = $index) {
            <span class="ar-scrub-dot" [class.active]="isActiveIndex(i)" [class.past]="i < activeIndex()"
                  [style.left.%]="dotLeft(i)" (click)="pickSnapshot(s, i)"></span>
          }
        </div>
      </div>

      @if (viewSnap()) {
        <span class="ar-pill-amber">Historical view</span>
      } @else {
        <span style="font-size:12.5px;color:#16a34a;font-weight:600">● Live working state</span>
      }
    </div>

    <!-- Collected action bar -->
    @if (collectedCount() > 0) {
      <div style="margin-top:14px;display:flex;align-items:center;gap:14px;background:#eff6ff;border:1px solid #dbeafe;border-radius:11px;padding:11px 16px">
        <i class="ki-filled ki-check-square" style="color:#3b82f6;font-size:18px"></i>
        <span style="font-size:13.5px;color:#1d4ed8"><b>{{ collectedCount() }}</b> files collected · {{ formatBytes(collectedBytes()) }}</span>
        <div style="margin-left:auto;display:flex;gap:8px">
          <button class="ar-btn-outline" (click)="clearCollected()">Clear</button>
          <button class="ar-btn-primary" (click)="restoreCollected()"><i class="ki-filled ki-cloud-download"></i>Restore collected</button>
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
                 class="grow bg-transparent outline-none text-[13px]" />
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
            <button class="ar-tree-row" [class.sel]="selectedFolder() === row.path" (click)="onTreeClick(row)"
                    style="display:flex;align-items:center;gap:5px;width:100%;font-size:13px"
                    [style.padding-left.px]="8 + row.depth * 18" [style.padding-top.px]="6" [style.padding-bottom.px]="6" [style.padding-right.px]="8">
              <i class="ki-filled ki-down" style="font-size:11px;color:#a1a1aa;transition:transform .12s"
                 [style.transform]="row.expanded ? 'rotate(0)' : 'rotate(-90deg)'" [style.visibility]="row.expandable ? 'visible' : 'hidden'"></i>
              <i class="ki-filled ki-folder" style="color:#fbbf24;font-size:15px"></i>
              <span style="color:#3f3f46;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{{ row.name }}</span>
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
                <div class="ar-file-row" (click)="toggleCollect(f)"
                     style="display:grid;grid-template-columns:34px 2fr 1.2fr .7fr .7fr .9fr;align-items:center;font-size:13px"
                     [style.height.px]="46" [style.background]="collected().has(f.relativePath) ? '#f7f9ff' : ''">
                  <div style="display:flex;justify-content:center">
                    <span class="ar-check" [class.on]="collected().has(f.relativePath)"><i class="ki-filled ki-check"></i></span>
                  </div>
                  <div class="flex items-center gap-2.5" style="min-width:0;padding-right:8px">
                    <i class="ki-outline ki-document" style="color:#a1a1aa;font-size:16px"></i>
                    <span style="color:#27272a;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{{ f.name }}</span>
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
    .ar-pill-green { font-size:10px;font-weight:700;color:#15803d;background:#f0fdf4;border-radius:999px;padding:1px 7px;letter-spacing:.04em }
    .ar-pill-amber { font-size:11.5px;font-weight:600;color:#b45309;background:#fffbeb;border:1px solid #fde68a;border-radius:999px;padding:3px 10px }
    .ar-snap-menu { position:absolute;top:46px;left:0;z-index:30;min-width:300px;background:#fff;border:1px solid #e4e4e7;border-radius:11px;box-shadow:0 12px 32px rgba(9,9,11,.14);padding:6px;max-height:320px;overflow-y:auto }
    .ar-snap-item { display:flex;align-items:center;gap:10px;width:100%;padding:8px 10px;border-radius:8px;font-size:12.5px;text-align:left }
    .ar-snap-item:hover { background:#f7f9ff }
    .ar-scrub-dot { position:absolute;top:50%;width:11px;height:11px;border-radius:999px;background:#d8dce2;transform:translate(-50%,-50%);cursor:pointer;border:2px solid #fff }
    .ar-scrub-dot.past { background:#bcd3f5 }
    .ar-scrub-dot.active { width:15px;height:15px;background:#3b82f6 }
    .ar-icon-btn { width:32px;height:32px;border-radius:8px;border:1px solid #e4e4e7;color:#71717a;display:flex;align-items:center;justify-content:center }
    .ar-icon-btn:disabled { opacity:.4 }
    .ar-tree-row.sel { background:#eff6ff }
    .ar-check { width:18px;height:18px;border-radius:5px;border:1.5px solid #d4d4d8;display:flex;align-items:center;justify-content:center;color:transparent;font-size:11px }
    .ar-check.on { background:#3b82f6;border-color:#3b82f6;color:#fff }
  `],
})
export class FilesTabComponent {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly drawer = inject(DrawerStore);

  readonly repoId = input.required<string>();

  protected readonly snapshots = signal<SnapshotDto[]>([]);
  protected readonly viewSnap = signal<string | null>(null);
  protected readonly pickerOpen = signal(false);
  protected readonly selectedFolder = signal('');
  protected readonly fileFilter = signal('');
  protected readonly collected = signal<Map<string, number>>(new Map());

  protected readonly expanded = signal<Set<string>>(new Set());
  protected readonly dirCache = signal<Map<string, EntryDto[]>>(new Map());
  protected readonly files = signal<EntryDto[]>([]);
  protected readonly filesLoading = signal(false);
  protected readonly filesError = signal<string | null>(null);

  protected readonly alias = signal('Repository');
  protected readonly container = signal('');

  protected readonly formatBytes = formatBytes;

  private currentRepo = -1;
  private filterTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    // React to repo + snapshot changes (input() read inside an effect-like via queueMicrotask polling avoided: use a getter).
    queueMicrotask(() => this.initIfNeeded());
  }

  private initIfNeeded(): void {
    const id = +this.repoId();
    if (id === this.currentRepo) return;
    this.currentRepo = id;
    this.api.getRepository(id).subscribe(r => { this.alias.set(r.alias); this.container.set(r.container); });
    this.api.getSnapshots(id).subscribe({ next: s => this.snapshots.set(s), error: () => this.snapshots.set([]) });
    this.resetToRoot();
  }

  private resetToRoot(): void {
    this.selectedFolder.set('');
    this.expanded.set(new Set());
    this.dirCache.set(new Map());
    void this.loadChildren('');
    void this.loadFiles('');
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
      const dirs = entries.filter(e => e.kind === 'dir');
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
      this.files.set(entries.filter(e => e.kind === 'file'));
    } catch (err: unknown) {
      this.filesError.set(err instanceof Error ? err.message : String(err));
    } finally {
      this.filesLoading.set(false);
    }
  }

  private fetch(prefix: string, filter: string | null): Promise<EntryDto[]> {
    return firstValueFrom(
      this.realtime.listEntries(+this.repoId(), {
        version: this.viewSnap(),
        prefix: prefix || null,
        filter,
        includeLocal: true,
      }).pipe(toArray()),
    );
  }

  // ── Filter ────────────────────────────────────────────────────────────────
  protected onFilter(value: string): void {
    this.fileFilter.set(value);
    clearTimeout(this.filterTimer);
    this.filterTimer = setTimeout(() => this.loadFiles(this.selectedFolder()), 250);
  }

  protected readonly shownFiles = computed(() => this.files());
  protected readonly shownBytes = computed(() => this.files().reduce((sum, f) => sum + (f.originalSize ?? 0), 0));

  // ── Snapshots / time-travel ─────────────────────────────────────────────
  protected readonly activeIndex = computed(() => {
    const v = this.viewSnap();
    if (!v) return 0;
    return Math.max(0, this.snapshots().findIndex(s => s.version === v));
  });
  protected isActiveIndex(i: number): boolean { return i === this.activeIndex(); }
  protected dotLeft(i: number): number {
    const n = this.snapshots().length;
    return n <= 1 ? 0 : (i / (n - 1)) * 100;
  }
  protected readonly activeSnapLabel = computed(() => {
    const list = this.snapshots();
    if (!list.length) return '—';
    const i = this.activeIndex();
    return 'v' + (list.length - i);
  });
  protected pickSnapshot(s: SnapshotDto, index: number): void {
    this.pickerOpen.set(false);
    this.viewSnap.set(index === 0 ? null : s.version);
    this.resetToRoot();
  }

  // ── Collect ────────────────────────────────────────────────────────────────
  protected toggleCollect(f: EntryDto): void {
    const next = new Map(this.collected());
    if (next.has(f.relativePath)) next.delete(f.relativePath);
    else next.set(f.relativePath, f.originalSize ?? 0);
    this.collected.set(next);
  }
  protected clearCollected(): void { this.collected.set(new Map()); }
  protected restoreCollected(): void {
    this.drawer.openRestore(+this.repoId(), this.viewSnap(), [...this.collected().keys()]);
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
