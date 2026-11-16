import { ChangeDetectionStrategy, Component, computed, inject, input, effect, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { JobPillStore } from '../../core/state/job-pill.store';
import { downloadContentUrl } from '../../core/api/api.service';
import { formatBytes } from '../../shared/format';
import { formatEta, formatThroughput, hydratedByLabel, phaseSentence } from '../../shared/job-format';

/** Floating repo-scoped progress pill (bottom-center of the content area). Dark, 30px SVG ring + two lines + "View job ›". */
@Component({
  selector: 'arius-job-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (store.visible() && (store.mode() === 'download-ready' || store.snapshot())) {
      <div data-testid="job-pill" style="position:fixed;left:50%;transform:translateX(-50%);bottom:16px;z-index:50;display:flex;align-items:center;gap:11px;
           background:#18181b;color:#fff;border-radius:999px;padding:9px 16px 9px 10px;box-shadow:0 12px 32px rgba(9,9,11,.28)">
        @if (store.mode() === 'download-ready') {
          <span style="width:30px;height:30px;border-radius:999px;background:#22c55e;display:flex;align-items:center;justify-content:center"><i class="ki-filled ki-check" style="font-size:14px"></i></span>
          <div style="line-height:1.35">
            <div style="font-size:12.5px;font-weight:600">Download ready</div>
            <div style="font-size:11px;color:#a1a1aa">{{ remainingLine() }}</div>
          </div>
        } @else if (rehydratingWithReady()) {
          <span style="width:30px;height:30px;border-radius:999px;background:#b45309;display:flex;align-items:center;justify-content:center"><i class="ki-filled ki-time" style="font-size:14px"></i></span>
          <div style="line-height:1.35">
            <div style="font-size:12.5px;font-weight:600">Rehydrating · {{ store.download()!.ready }} of {{ store.download()!.total }} ready</div>
            <div style="font-size:11px;color:#a1a1aa">{{ hydratedBy() || 'Waiting on Azure' }}</div>
          </div>
        } @else if (store.snapshot(); as s) {
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
        }
        @if (showDownloadLink()) {
          <a data-testid="pill-download" [attr.href]="downloadHref()" (click)="store.markDownloaded()" download style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;color:#60a5fa;text-decoration:none;margin-left:4px">
            <i class="ki-filled ki-exit-down" style="font-size:13px"></i>{{ store.mode() === 'download-ready' ? 'Download' : 'Download ready' }}</a>
        }
        <a data-testid="pill-open" [routerLink]="['/jobs', store.jobId()]" [style.color]="downloadPill() ? '#a1a1aa' : '#60a5fa'" style="font-size:12px;font-weight:600;text-decoration:none;margin-left:4px">View job ›</a>
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
  protected readonly rehydratingWithReady = computed(() =>
    this.store.kind() === 'download' && this.store.mode() === 'progress' && this.store.status() === 'rehydrating' && (this.store.download()?.ready ?? 0) > 0);
  /** The link is offered while files are ready and not yet downloaded. */
  protected readonly showDownloadLink = computed(() =>
    this.store.mode() === 'download-ready' || (this.rehydratingWithReady() && (this.store.download()?.remaining ?? 0) > 0));
  protected readonly downloadPill = computed(() => this.store.mode() === 'download-ready' || this.rehydratingWithReady());
  protected readonly downloadHref = computed(() => downloadContentUrl(this.store.jobId()!, 'remaining'));
  protected readonly hydratedBy = computed(() => {
    const r = this.store.resume();
    return r ? hydratedByLabel(r.rehydrationStartedAt, r.rehydrationWindowHours) : '';
  });
  protected readonly remainingLine = computed(() => {
    const d = this.store.download();
    return d ? `${d.remaining} remaining ${d.remaining === 1 ? 'file' : 'files'} · ${formatBytes(d.remainingBytes)}` : '';
  });
  protected line2 = () => {
    const s = this.store.snapshot()!;
    return `${formatEta(s.etaSeconds)} · ${formatThroughput(s.throughputBytesPerSec)}`;
  };
}
