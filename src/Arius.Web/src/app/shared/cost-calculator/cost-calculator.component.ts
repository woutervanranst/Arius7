import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatisticsDto } from '../../core/api/api-models';
import { formatBytes, formatCount, formatCurrency } from '../format';

/**
 * Region-aware storage-cost breakdown by tier — the shared "CostCalculator" view that replaces the old
 * "Stored size by tier" section on the Statistics tab. Renders a 100%-stacked hero bar, a per-tier detail
 * table (stored size, chunks, est. cost/mo) and a grand-total footer. It does no arithmetic: per-tier and
 * total costs are computed server-side by the CostQuery/StatisticsQuery handler and arrive on the DTO.
 */
@Component({
  selector: 'arius-cost-calculator',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rows().length) {
      <div class="ar-card" data-testid="tier-breakdown" style="padding:20px 22px">
        <!-- Header -->
        <div class="flex items-baseline justify-between">
          <div style="font-size:13px;font-weight:600;color:#3f3f46">Stored size by tier</div>
          <div style="font-size:12px;color:#a1a1aa">{{ totalStoredLabel() }} stored</div>
        </div>

        <!-- Hero 100%-stacked bar -->
        <div style="display:flex;height:34px;border-radius:8px;overflow:hidden;margin:14px 0 20px">
          @for (r of rows(); track r.tier) {
            <div [style.width.%]="r.pct" [style.background]="r.color"
                 style="display:flex;align-items:center;justify-content:center">
              @if (r.showLabel) {
                <span style="color:#fff;font-size:11px;font-weight:600">{{ r.pctLabel }}</span>
              }
            </div>
          }
        </div>

        <!-- Detail table -->
        <div style="display:grid;grid-template-columns:16px 1fr 110px 130px 110px;gap:14px;align-items:center;padding-bottom:10px;border-bottom:1px solid #f1f1f4">
          <div></div>
          <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.04em">Tier</div>
          <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.04em;text-align:right">Stored</div>
          <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.04em;text-align:right">Chunks</div>
          <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.04em;text-align:right">Est. cost/mo</div>
        </div>

        @for (r of rows(); track r.tier; let last = $last) {
          <div data-testid="tier-row"
               style="display:grid;grid-template-columns:16px 1fr 110px 130px 110px;gap:14px;align-items:center;padding:13px 0"
               [style.border-bottom]="last ? 'none' : '1px solid #f6f6f7'">
            <span style="width:11px;height:11px;border-radius:3px" [style.background]="r.color"></span>
            <div style="font-size:13.5px;font-weight:600;color:#27272a">{{ r.tier }}</div>
            <div style="font-size:13px;font-weight:600;color:#27272a;text-align:right">{{ r.size }}</div>
            <div style="font-size:12.5px;color:#a1a1aa;text-align:right">{{ r.chunks }}</div>
            <div data-testid="tier-cost" style="font-size:13px;font-weight:600;color:#27272a;text-align:right">{{ r.cost }}</div>
          </div>
        }

        <!-- Grand-total footer -->
        <div style="display:grid;grid-template-columns:16px 1fr 110px 130px 110px;gap:14px;align-items:center;padding:14px 0 2px;border-top:2px solid #f1f1f4;margin-top:2px">
          <div></div>
          <div style="font-size:13px;font-weight:700;color:#3f3f46">Total est. monthly cost</div>
          <div></div>
          <div></div>
          <div data-testid="total-cost" style="font-size:16px;font-weight:700;color:#18181b;text-align:right">{{ totalCostLabel() }}</div>
        </div>
      </div>
    }
  `,
})
export class CostCalculatorComponent {
  /** The repository statistics (carries per-tier stored size, chunk counts and the server-computed cost). */
  readonly stats = input<StatisticsDto | null>(null);

  // Warm → cold, matching the existing Statistics-tab palette (and the design handoff's TIER_COLORS).
  private static readonly TIER_COLORS: Record<string, string> = {
    Hot: '#ef4444', Cool: '#3b82f6', Cold: '#0ea5e9', Archive: '#64748b',
  };

  // Segments narrower than this don't get an inline percentage label (it wouldn't fit legibly).
  private static readonly LABEL_MIN_PCT = 8;

  protected readonly totalStored = computed(() =>
    (this.stats()?.storedByTier ?? []).reduce((sum, t) => sum + t.storedSize, 0));

  protected readonly rows = computed(() => {
    const s = this.stats();
    const total = this.totalStored();
    return (s?.storedByTier ?? []).map(t => {
      const pct = total > 0 ? (t.storedSize / total) * 100 : 0;
      return {
        tier: t.tier,
        color: CostCalculatorComponent.TIER_COLORS[t.tier] ?? '#a1a1aa',
        size: formatBytes(t.storedSize),
        chunks: formatCount(t.uniqueChunks),
        cost: formatCurrency(t.costPerMonth),
        pct,
        showLabel: pct >= CostCalculatorComponent.LABEL_MIN_PCT,
        pctLabel: `${Math.round(pct)}%`,
      };
    });
  });

  protected readonly totalStoredLabel = computed(() => formatBytes(this.totalStored()));
  protected readonly totalCostLabel = computed(() =>
    formatCurrency(this.stats()?.totalStorageCostPerMonth ?? 0));
}
