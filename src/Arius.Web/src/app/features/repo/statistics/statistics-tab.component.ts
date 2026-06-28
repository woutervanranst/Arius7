import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService } from '../../../core/api/api.service';
import { SnapshotStore } from '../../../core/state/snapshot.store';
import { StatisticsDto } from '../../../core/api/api-models';
import { formatBytes, formatCount } from '../../../shared/format';
import { CostCalculatorComponent } from '../../../shared/cost-calculator/cost-calculator.component';

/**
 * Statistics tab. Two scopes are shown separately because they answer different questions:
 * "This snapshot" (logical size of the snapshot selected in the bar above the tabs — loaded immediately)
 * vs "Repository storage" (deduplicated + compressed footprint across all snapshots, from the chunk
 * index). The storage figures are lazy-loaded with full chunk-index coverage (server-side), so they are
 * complete rather than reflecting only what browsing happened to cache — a spinner shows while they load.
 */
@Component({
  selector: 'arius-statistics-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CostCalculatorComponent],
  template: `
    <!-- Section labels aligned over the unified card row (2 snapshot + 3 storage columns). -->
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:14px;margin-bottom:10px">
      <div data-testid="section-snapshot" style="grid-column:span 2;font-size:13px;font-weight:600;color:#3f3f46">This snapshot</div>
      <div data-testid="section-storage" style="grid-column:span 3;font-size:13px;font-weight:600;color:#3f3f46">
        Repository storage <span style="font-weight:400;color:#a1a1aa">· across all snapshots</span>
      </div>
    </div>

    <!-- All five KPI cards in a single row. -->
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:14px">
      @for (card of snapshotCards(); track card.label) {
        <div class="ar-card" data-testid="kpi-card" style="padding:15px 16px">
          <div style="width:36px;height:36px;border-radius:10px;display:flex;align-items:center;justify-content:center" [style.background]="card.chipBg" [style.color]="card.chipFg">
            <i class="ki-filled {{ card.icon }}" style="font-size:17px"></i>
          </div>
          <div style="font-size:21px;font-weight:700;color:#18181b;margin-top:10px;line-height:1">{{ card.value }}</div>
          <div style="font-size:12.5px;color:#71717a;margin-top:3px" [title]="card.hint">{{ card.label }}</div>
        </div>
      }
      @if (storageLoading()) {
        <!-- Spinner spans the three storage columns until the full-coverage figures are ready. -->
        <div class="ar-card" data-testid="storage-loading" style="grid-column:span 3;padding:15px 16px;display:flex;align-items:center;justify-content:center;gap:10px;color:#71717a">
          <span class="ar-spinner"></span>
          <span style="font-size:12.5px">Calculating across all snapshots…</span>
        </div>
      } @else if (storageError()) {
        <div class="ar-card" data-testid="storage-error" style="grid-column:span 3;padding:15px 16px;display:flex;align-items:center;gap:8px;color:#b45309;font-size:12.5px">
          <i class="ki-filled ki-information-2"></i> Could not load repository storage figures.
        </div>
      } @else {
        @for (card of storageCards(); track card.label) {
          <div class="ar-card" data-testid="kpi-card" style="padding:15px 16px">
            <div style="width:36px;height:36px;border-radius:10px;display:flex;align-items:center;justify-content:center" [style.background]="card.chipBg" [style.color]="card.chipFg">
              <i class="ki-filled {{ card.icon }}" style="font-size:17px"></i>
            </div>
            <div style="font-size:21px;font-weight:700;color:#18181b;margin-top:10px;line-height:1">{{ card.value }}</div>
            <div style="font-size:12.5px;color:#71717a;margin-top:3px" [title]="card.hint">{{ card.label }}</div>
          </div>
        }
      }
    </div>

    @if (!storageLoading() && (storageStats()?.storedByTier?.length ?? 0)) {
      <div style="margin-top:18px">
        <arius-cost-calculator [stats]="storageStats()" />
      </div>
    }

  `,
  styles: [`
    .ar-spinner { width:16px;height:16px;border-radius:999px;border:2px solid #e4e4e7;border-top-color:#6d28d9;display:inline-block;animation:ar-spin .7s linear infinite }
    @keyframes ar-spin { to { transform:rotate(360deg) } }
  `],
})
export class StatisticsTabComponent {
  private readonly api = inject(ApiService);
  private readonly snap = inject(SnapshotStore);
  readonly repoId = input.required<string>();

  // Per-snapshot figures (fast); follows the selected snapshot.
  protected readonly snapStats = signal<StatisticsDto | null>(null);
  // Repository-wide figures (lazy, full chunk-index coverage); repo-scoped, version-independent.
  protected readonly storageStats = signal<StatisticsDto | null>(null);
  protected readonly storageLoading = signal(false);
  protected readonly storageError = signal(false);

  constructor() {
    // Snapshot figures: reload when the repo or the shared snapshot selection (bar above the tabs) changes.
    effect(onCleanup => {
      const id = +this.repoId();
      const version = this.snap.version();
      this.snapStats.set(null);
      const sub = this.api.getStatistics(id, version).subscribe({ next: s => this.snapStats.set(s), error: () => this.snapStats.set(null) });
      onCleanup(() => sub.unsubscribe());
    });

    // Repository-storage figures: lazy-load once per repo with full chunk-index coverage (slow), so the
    // numbers are complete instead of reflecting only browsed coverage. Repo-wide, so version is ignored.
    effect(onCleanup => {
      const id = +this.repoId();
      this.storageStats.set(null);
      this.storageError.set(false);
      this.storageLoading.set(true);
      const sub = this.api.getStatistics(id, null, true).subscribe({
        next: s => { this.storageStats.set(s); this.storageLoading.set(false); },
        error: () => { this.storageError.set(true); this.storageLoading.set(false); },
      });
      onCleanup(() => sub.unsubscribe());
    });

    // SnapshotStore is normally primed by the bar in the repo shell; prime it too in case Statistics is
    // the first tab rendered for a repo (deep link), so version resolves to the latest snapshot.
    effect(() => {
      const id = +this.repoId();
      untracked(() => this.snap.load(id));
    });
  }

  // Logical metrics for the selected snapshot (from its manifest).
  protected snapshotCards() {
    const s = this.snapStats();
    return [
      { label: 'Files', value: s ? formatCount(s.files) : '—', hint: 'Number of files in this snapshot.', icon: 'ki-document', chipBg: '#eff6ff', chipFg: '#3b82f6' },
      { label: 'Original size', value: s ? formatBytes(s.originalSize) : '—', hint: 'Total uncompressed size of all files in this snapshot (the size you would restore).', icon: 'ki-data', chipBg: '#f0fdf4', chipFg: '#15803d' },
    ];
  }

  // Physical metrics for the repository (deduplicated + compressed, from the full chunk index).
  protected storageCards() {
    const s = this.storageStats();
    return [
      { label: 'Deduplicated size', value: s ? formatBytes(s.deduplicatedSize) : '—', hint: 'Unique data before compression — duplicate content counted once.', icon: 'ki-copy', chipBg: '#fefce8', chipFg: '#ca8a04' },
      { label: 'Stored size', value: s ? formatBytes(s.storedSize) : '—', hint: 'Actual cloud storage footprint — deduplicated and compressed.', icon: 'ki-cloud', chipBg: '#f5f3ff', chipFg: '#6d28d9' },
      { label: 'Unique chunks', value: s ? formatCount(s.uniqueChunks) : '—', hint: 'Number of distinct chunks stored.', icon: 'ki-element-11', chipBg: '#fffbeb', chipFg: '#b45309' },
    ];
  }

}
