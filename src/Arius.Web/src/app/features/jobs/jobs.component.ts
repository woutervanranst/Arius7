import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api/api.service';
import { RealtimeService } from '../../core/api/realtime.service';
import { JobDto, LogLine } from '../../core/api/api-models';
import { LiveConsoleComponent } from '../../shared/live-console/live-console.component';

const STATUS: Record<string, { label: string; color: string; bg: string; icon: string }> = {
  running:     { label: 'Running',     color: '#1d4ed8', bg: '#eff6ff', icon: 'ki-loading' },
  rehydrating: { label: 'Rehydrating', color: '#b45309', bg: '#fffbeb', icon: 'ki-time' },
  scheduled:   { label: 'Scheduled',   color: '#6d28d9', bg: '#f5f3ff', icon: 'ki-calendar-tick' },
  queued:      { label: 'Queued',      color: '#52525b', bg: '#f4f4f5', icon: 'ki-time' },
  completed:   { label: 'Completed',   color: '#15803d', bg: '#f0fdf4', icon: 'ki-check-circle' },
  failed:      { label: 'Failed',      color: '#dc2626', bg: '#fef2f2', icon: 'ki-cross-circle' },
};

/** Jobs: the runs table (one-off + scheduled) and the unified live console feed. */
@Component({
  selector: 'arius-jobs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, LiveConsoleComponent],
  template: `
    <div class="flex items-center gap-3">
      <h1 class="ar-heading" style="font-size:22px;font-weight:700">Jobs</h1>
      <span class="ar-pill" style="background:#eff6ff;color:#1d4ed8">{{ runningCount() }} running</span>
      <span class="ar-pill" style="background:#f5f3ff;color:#6d28d9">{{ scheduledCount() }} scheduled</span>
    </div>

    <div class="ar-card" style="margin-top:20px;padding:0;overflow:hidden">
      <div style="display:grid;grid-template-columns:2.2fr 1.5fr 1fr 1.5fr;padding:11px 20px;font-size:11px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">
        <div>Job</div><div>Trigger</div><div>Status</div><div>Progress</div>
      </div>
      @if (jobs(); as list) {
        @for (job of list; track job.id) {
          <div data-testid="job-row" style="display:grid;grid-template-columns:2.2fr 1.5fr 1fr 1.5fr;align-items:center;padding:12px 20px;border-top:1px solid #f6f6f7"
               [style.opacity]="job.status === 'completed' ? .66 : 1">
            <div class="flex items-center gap-3">
              <div style="width:34px;height:34px;border-radius:9px;display:flex;align-items:center;justify-content:center"
                   [style.background]="job.kind === 'archive' ? '#eff6ff' : '#f5f3ff'" [style.color]="job.kind === 'archive' ? '#3b82f6' : '#6d28d9'">
                <i class="ki-filled {{ job.kind === 'archive' ? 'ki-cloud-add' : 'ki-cloud-download' }}" style="font-size:16px"></i>
              </div>
              <div>
                <div style="font-size:13.5px;font-weight:600;color:#27272a">{{ job.repo }}</div>
                <div style="font-size:12px;color:#a1a1aa;text-transform:capitalize">{{ job.kind }}</div>
              </div>
            </div>
            <div style="font-size:13px;color:#52525b;text-transform:capitalize">
              <i class="ki-filled {{ job.trigger === 'schedule' ? 'ki-calendar' : 'ki-flash' }}" style="color:#a1a1aa;margin-right:5px"></i>{{ job.trigger }}
            </div>
            <div>
              <span class="ar-pill" data-testid="job-status" [style.color]="meta(job.status).color" [style.background]="meta(job.status).bg">
                <i class="ki-filled {{ meta(job.status).icon }}" style="font-size:12px"></i>{{ meta(job.status).label }}
              </span>
            </div>
            <div>
              <div style="height:5px;background:#eef0f3;border-radius:999px;overflow:hidden;max-width:160px">
                <div style="height:100%;background:#3b82f6" [style.width.%]="job.pct"></div>
              </div>
              <div style="font-size:11.5px;color:#a1a1aa;margin-top:4px">
                {{ job.detail || (job.finishedAt ? ('finished ' + (job.finishedAt | date:'dd MMM HH:mm')) : 'in progress') }}
              </div>
            </div>
          </div>
        } @empty {
          <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">No jobs yet.</div>
        }
      } @else {
        <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
      }
    </div>

    <div style="margin-top:18px">
      <div style="font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:8px">Live output</div>
      <arius-live-console [lines]="consoleLines()" [height]="300" />
    </div>
  `,
  styles: [`.ar-pill { display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px }`],
})
export class JobsComponent {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  protected readonly jobs = toSignal(this.api.getJobs());
  protected readonly consoleLines = signal<LogLine[]>([]);

  protected readonly runningCount = computed(() => this.jobs()?.filter(j => j.status === 'running' || j.status === 'rehydrating').length ?? 0);
  protected readonly scheduledCount = computed(() => this.jobs()?.filter(j => j.status === 'scheduled').length ?? 0);

  constructor() {
    this.realtime.log$
      .pipe(takeUntilDestroyed())
      .subscribe(line => this.consoleLines.update(a => [...a.slice(-200), line]));
  }

  protected meta(status: string) {
    return STATUS[status] ?? STATUS['queued'];
  }
}
