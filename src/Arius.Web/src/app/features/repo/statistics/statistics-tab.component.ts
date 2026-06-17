import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { ApiService } from '../../../core/api/api.service';
import { StatsDto } from '../../../core/api/api-models';
import { formatBytes, formatCount } from '../../../shared/format';

/** Statistics tab: Files / Original size / Stored size / Unique chunks, with the "pending" banner. */
@Component({
  selector: 'arius-statistics-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:18px">
      @for (card of cards(); track card.label) {
        <div class="ar-card" data-testid="kpi-card" style="padding:19px 20px">
          <div style="width:42px;height:42px;border-radius:11px;display:flex;align-items:center;justify-content:center" [style.background]="card.chipBg" [style.color]="card.chipFg">
            <i class="ki-filled {{ card.icon }}" style="font-size:20px"></i>
          </div>
          <div style="font-size:24px;font-weight:700;color:#18181b;margin-top:12px;line-height:1">{{ card.value }}</div>
          <div style="font-size:13px;color:#71717a;margin-top:4px">{{ card.label }}</div>
        </div>
      }
    </div>

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
  readonly repoId = input.required<string>();

  protected readonly stats = signal<StatsDto | null>(null);

  constructor() {
    // Reload when repoId changes — the router reuses this component across /repos/:id navigations.
    effect(onCleanup => {
      const id = +this.repoId();
      this.stats.set(null);
      const sub = this.api.getStats(id).subscribe({ next: s => this.stats.set(s), error: () => this.stats.set(null) });
      onCleanup(() => sub.unsubscribe());   // cancel the in-flight request if repoId changes first
    });
  }

  protected cards() {
    const s = this.stats();
    return [
      { label: 'Files', value: s ? formatCount(s.files) : '—', icon: 'ki-document', chipBg: '#eff6ff', chipFg: '#3b82f6' },
      { label: 'Original size', value: s ? formatBytes(s.originalSize) : '—', icon: 'ki-data', chipBg: '#f0fdf4', chipFg: '#15803d' },
      { label: 'Stored size', value: s ? formatBytes(s.storedSize) : '—', icon: 'ki-cloud', chipBg: '#f5f3ff', chipFg: '#6d28d9' },
      { label: 'Unique chunks', value: s ? formatCount(s.uniqueChunks) : '—', icon: 'ki-element-11', chipBg: '#fffbeb', chipFg: '#b45309' },
    ];
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
