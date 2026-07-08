import { ChangeDetectionStrategy, Component, computed, inject, input, effect, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { JobPillStore } from '../../core/state/job-pill.store';
import { formatEta, formatThroughput, phaseSentence } from '../../shared/job-format';

/** Floating repo-scoped progress pill (bottom-center of the content area). Dark, 30px SVG ring + two lines + "View job ›". */
@Component({
  selector: 'arius-job-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (store.visible() && store.snapshot(); as s) {
      <div data-testid="job-pill" style="position:fixed;left:50%;transform:translateX(-50%);bottom:16px;z-index:50;display:flex;align-items:center;gap:11px;
           background:#18181b;color:#fff;border-radius:999px;padding:9px 16px 9px 10px;box-shadow:0 12px 32px rgba(9,9,11,.28)">
        <!-- 30px progress ring -->
        <svg width="30" height="30" viewBox="0 0 30 30" style="transform:rotate(-90deg)">
          <circle cx="15" cy="15" r="13" fill="none" stroke="#3f3f46" stroke-width="4"></circle>
          <circle cx="15" cy="15" r="13" fill="none" stroke="#60a5fa" stroke-width="4" stroke-linecap="round"
                  [attr.stroke-dasharray]="circumference"
                  [attr.stroke-dashoffset]="circumference * (1 - s.pct / 100)"></circle>
        </svg>
        <div style="line-height:1.35">
          <div style="font-size:12.5px;font-weight:600">{{ verb() }} · {{ s.pct }}%</div>
          <div style="font-size:11px;color:#a1a1aa">{{ line2() }}</div>
        </div>
        <a data-testid="pill-open" [routerLink]="['/jobs', store.jobId()]" style="font-size:12px;font-weight:600;color:#60a5fa;text-decoration:none;margin-left:4px">View job ›</a>
        <button data-testid="pill-hide" (click)="store.dismiss()" style="width:22px;height:22px;border-radius:999px;color:#71717a;display:flex;align-items:center;justify-content:center"><i class="ki-filled ki-cross" style="font-size:11px"></i></button>
      </div>
    }
  `,
})
export class JobPillComponent implements OnDestroy {
  protected readonly store = inject(JobPillStore);
  readonly repoId = input.required<number>();
  protected readonly circumference = 2 * Math.PI * 13;

  constructor() {
    effect(() => { const id = this.repoId(); if (id) this.store.discover(id); });
  }

  ngOnDestroy(): void { this.store.detach(); }

  protected readonly verb = computed(() => {
    const s = this.store.snapshot()!; const kind = this.store.kind();
    return phaseSentence(s, kind).split(' —')[0];   // "Uploading" / "Restoring" style verb
  });
  protected line2 = () => {
    const s = this.store.snapshot()!;
    return `${formatEta(s.etaSeconds)} · ${formatThroughput(s.throughputBytesPerSec)}`;
  };
}
