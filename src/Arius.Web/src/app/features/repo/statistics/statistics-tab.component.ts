import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService } from '../../../core/api/api.service';
import { SnapshotStore } from '../../../core/state/snapshot.store';
import { StatisticsDto } from '../../../core/api/api-models';
import { formatBytes, formatCount } from '../../../shared/format';

/**
 * Statistics tab. Two scopes are shown separately because they answer different questions and have
 * different scope: "This snapshot" (logical size of the snapshot selected in the bar above the tabs)
 * vs "Repository storage" (deduplicated + compressed footprint across all snapshots, from the chunk
 * index). The snapshot scope follows the shared SnapshotStore selection.
 */
@Component({
  selector: 'arius-statistics-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
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
      @for (card of storageCards(); track card.label) {
        <div class="ar-card" data-testid="kpi-card" style="padding:15px 16px">
          <div style="width:36px;height:36px;border-radius:10px;display:flex;align-items:center;justify-content:center" [style.background]="card.chipBg" [style.color]="card.chipFg">
            <i class="ki-filled {{ card.icon }}" style="font-size:17px"></i>
          </div>
          <div style="font-size:21px;font-weight:700;color:#18181b;margin-top:10px;line-height:1">{{ card.value }}</div>
          <div style="font-size:12.5px;color:#71717a;margin-top:3px" [title]="card.hint">{{ card.label }}</div>
        </div>
      }
    </div>

    @if (savings(); as sv) {
      <div data-testid="savings" class="ar-card" style="margin-top:18px;padding:14px 20px;background:#f0fdf4;border-color:#bbf7d0">
        <div style="font-size:13px;color:#15803d;line-height:1.5">
          <i class="ki-filled ki-discount" style="color:#15803d"></i>
          Stored <strong>{{ sv.stored }}</strong> from <strong>{{ sv.original }}</strong> of files —
          <strong>{{ sv.percent }}</strong> smaller after deduplication and compression.
        </div>
      </div>
    }

    @if (tiers().length) {
      <div class="ar-card" data-testid="tier-breakdown" style="margin-top:18px;padding:18px 20px">
        <div style="font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:14px">Stored size by tier</div>
        <div style="display:flex;flex-direction:column;gap:11px">
          @for (t of tiers(); track t.tier) {
            <div class="flex items-center gap-3" data-testid="tier-row">
              <span style="width:62px;font-size:12.5px;font-weight:600" [style.color]="t.color">{{ t.tier }}</span>
              <div style="flex:1;height:8px;border-radius:5px;background:#f1f1f4;overflow:hidden">
                <div style="height:100%;border-radius:5px" [style.width.%]="t.pct" [style.background]="t.color"></div>
              </div>
              <span style="width:84px;text-align:right;font-size:13px;color:#27272a;font-weight:600">{{ t.size }}</span>
              <span style="width:96px;text-align:right;font-size:12px;color:#a1a1aa">{{ t.chunks }} chunks</span>
            </div>
          }
        </div>
      </div>
    }

    <div class="ar-card" style="margin-top:18px;padding:16px 20px;background:#f7f9ff;border-color:#dbeafe">
      <div style="font-size:13px;color:#3f3f46;line-height:1.5">
        <i class="ki-filled ki-information-2" style="color:#3b82f6"></i>
        Counts are derived from the file-tree and chunk index. Figures finalise once the local cache
        has fully downloaded.
      </div>
    </div>
  `,
})
export class StatisticsTabComponent {
  private readonly api = inject(ApiService);
  private readonly snap = inject(SnapshotStore);
  readonly repoId = input.required<string>();

  protected readonly stats = signal<StatisticsDto | null>(null);

  constructor() {
    // Reload when the repo or the shared snapshot selection (the bar above the tabs) changes.
    effect(onCleanup => {
      const id = +this.repoId();
      const version = this.snap.version();
      this.stats.set(null);
      const sub = this.api.getStatistics(id, version).subscribe({ next: s => this.stats.set(s), error: () => this.stats.set(null) });
      onCleanup(() => sub.unsubscribe());   // cancel the in-flight request if the inputs change first
    });
    // SnapshotStore is normally primed by the bar in the repo shell; prime it too in case Statistics
    // is the first tab rendered for a repo (deep link), so version resolves to the latest snapshot.
    effect(() => {
      const id = +this.repoId();
      untracked(() => this.snap.load(id));
    });
  }

  // Logical metrics for the selected snapshot (from its manifest).
  protected snapshotCards() {
    const s = this.stats();
    return [
      { label: 'Files', value: s ? formatCount(s.files) : '—', hint: 'Number of files in this snapshot.', icon: 'ki-document', chipBg: '#eff6ff', chipFg: '#3b82f6' },
      { label: 'Original size', value: s ? formatBytes(s.originalSize) : '—', hint: 'Total uncompressed size of all files in this snapshot (the size you would restore).', icon: 'ki-data', chipBg: '#f0fdf4', chipFg: '#15803d' },
    ];
  }

  // Physical metrics for the repository (deduplicated + compressed, from the chunk index).
  protected storageCards() {
    const s = this.stats();
    return [
      { label: 'Deduplicated size', value: s ? formatBytes(s.deduplicatedSize) : '—', hint: 'Unique data before compression — duplicate content counted once.', icon: 'ki-copy', chipBg: '#fefce8', chipFg: '#ca8a04' },
      { label: 'Stored size', value: s ? formatBytes(s.storedSize) : '—', hint: 'Actual cloud storage footprint — deduplicated and compressed.', icon: 'ki-cloud', chipBg: '#f5f3ff', chipFg: '#6d28d9' },
      { label: 'Unique chunks', value: s ? formatCount(s.uniqueChunks) : '—', hint: 'Number of distinct chunks stored.', icon: 'ki-element-11', chipBg: '#fffbeb', chipFg: '#b45309' },
    ];
  }

  // Combined deduplication + compression reduction, original (logical) → stored (physical).
  // Only meaningful on the latest snapshot: stored/dedup are repo-wide, so comparing them against a
  // single historical snapshot's (smaller) original size would overstate or invert the ratio.
  protected savings() {
    if (this.snap.version()) return null;
    const s = this.stats();
    if (!s || s.originalSize <= 0 || s.storedSize <= 0 || s.storedSize >= s.originalSize) return null;
    return {
      original: formatBytes(s.originalSize),
      stored: formatBytes(s.storedSize),
      percent: `${Math.round((1 - s.storedSize / s.originalSize) * 100)}%`,
    };
  }

  // Warmer → cooler colours so the access-tier story reads at a glance (Archive = coldest = slowest to restore).
  private static readonly TIER_COLORS: Record<string, string> = {
    Hot: '#ef4444', Cool: '#3b82f6', Cold: '#0ea5e9', Archive: '#64748b',
  };

  protected tiers() {
    const rows = this.stats()?.storedByTier ?? [];
    const max = Math.max(1, ...rows.map(t => t.storedSize));
    return rows.map(t => ({
      tier: t.tier,
      size: formatBytes(t.storedSize),
      chunks: formatCount(t.uniqueChunks),
      pct: (t.storedSize / max) * 100,
      color: StatisticsTabComponent.TIER_COLORS[t.tier] ?? '#a1a1aa',
    }));
  }
}
