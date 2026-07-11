import { ChangeDetectionStrategy, Component, computed, effect, inject, OnDestroy, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { forkJoin, of, Subscription } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';
import { ApiService } from '../../core/api/api.service';
import { RealtimeService } from '../../core/api/realtime.service';
import { JobDto, JobOutcome, JobSnapshot, ScheduleDto, isNonTerminal } from '../../core/api/api-models';
import { LayeredBarComponent } from '../../shared/layered-bar/layered-bar.component';
import { formatBytes, formatCount } from '../../shared/format';
import { formatDuration, formatEta, phaseSentence, StatusMeta, statusMeta, archiveBarLayers, restoreBarLayers } from '../../shared/job-format';

/** "04 Jul 14:02" — used for the "ran …" meta line and the history one-liner's snapshot timestamp. */
function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  const day = String(d.getDate()).padStart(2, '0');
  const month = d.toLocaleString('en-US', { month: 'short' });
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  return `${day} ${month} ${hh}:${mm}`;
}

/** Guards `JSON.parse` of the `outcome` column — malformed/legacy rows must not blow up the list. */
function parseOutcome(raw: string | null): JobOutcome | null {
  if (!raw) return null;
  try { return JSON.parse(raw) as JobOutcome; } catch { return null; }
}

interface ActiveRow {
  job: JobDto;
  kind: 'archive' | 'restore';
  scanned: number;
  middle: number;
  top: number;
  phase: string;
  eta: string;
  meta: StatusMeta;
}

interface HistoryRow {
  job: JobDto;
  kindLabel: string;
  summary: string;
  meta: StatusMeta;
  actionLabel: string;
}

/**
 * Jobs overview: sectioned list — Needs your attention (awaiting-cost), Active (running/rehydrating,
 * live per-row mini-bar via SignalR attach), and Scheduled & history (terminal jobs, one-line outcome).
 * Each job's own log/progress lives on its detail page (`/jobs/:id`), reachable via Detail ›.
 */
@Component({
  selector: 'arius-jobs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, LayeredBarComponent],
  template: `
    @if (jobs(); as list) {
      <div class="flex items-center gap-3">
        <h1 class="ar-heading" style="font-size:22px;font-weight:700">Jobs</h1>
        <span style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px;background:#eff6ff;color:#1d4ed8">{{ runningCount() }} running</span>
        <span style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px;background:#fffbeb;color:#b45309;border:1px solid #fde68a">{{ waitingCount() }} waiting</span>
        <span style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px;background:#f5f3ff;color:#6d28d9">{{ scheduledCount() }} scheduled</span>
      </div>

      <!-- Needs your attention -->
      @if (needsAttention().length > 0) {
        <div style="margin-top:18px;font-size:11.5px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">Needs your attention</div>
        <div data-testid="jobs-needs-attention" style="margin-top:8px;display:flex;flex-direction:column;gap:8px">
          @for (job of needsAttention(); track job.id) {
            <div data-testid="job-row" style="display:flex;align-items:center;gap:14px;border:1px solid #fde68a;background:#fffbeb;border-radius:13px;padding:14px 18px">
              <div style="width:38px;height:38px;border-radius:10px;background:#fff;color:#b45309;border:1px solid #fde68a;display:flex;align-items:center;justify-content:center">
                <i class="ki-filled ki-dollar" style="font-size:17px"></i>
              </div>
              <div style="flex:1">
                <div style="font-size:13.5px;font-weight:600;color:#92400e">{{ kindLabel(job) }} &middot; {{ job.repo }} — waiting for cost confirmation</div>
                <div style="font-size:12.5px;color:#a16207;margin-top:2px">{{ job.detail || 'Job is paused until you choose a priority.' }}</div>
              </div>
              <a data-testid="job-review-cost" [routerLink]="['/jobs', job.id]"
                 style="height:38px;padding:0 16px;border-radius:9px;font-size:13px;font-weight:600;background:#b45309;color:#fff;text-decoration:none;display:inline-flex;align-items:center">Review cost &rsaquo;</a>
            </div>
          }
        </div>
      }

      <!-- Active -->
      <div style="margin-top:22px;font-size:11.5px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">Active</div>
      <div data-testid="jobs-active" style="margin-top:8px;border:1px solid #ececef;border-radius:13px;overflow:hidden">
        @for (row of activeRows(); track row.job.id; let first = $first) {
          <div data-testid="job-row" style="display:grid;grid-template-columns:260px 1fr 190px 110px;align-items:center;gap:18px;padding:15px 20px"
               [style.borderTop]="first ? 'none' : '1px solid #f6f6f7'">
            <div style="display:flex;align-items:center;gap:12px">
              <div style="width:36px;height:36px;border-radius:10px;display:flex;align-items:center;justify-content:center"
                   [style.background]="row.kind === 'restore' ? '#f5f3ff' : '#eff6ff'" [style.color]="row.kind === 'restore' ? '#6d28d9' : '#3b82f6'">
                <i class="ki-filled" [class.ki-cloud-download]="row.kind === 'restore'" [class.ki-cloud-add]="row.kind !== 'restore'" style="font-size:16px"></i>
              </div>
              <div>
                <div style="font-size:13.5px;font-weight:600;color:#27272a">{{ kindLabel(row.job) }} &middot; {{ row.job.repo }}</div>
                <div style="font-size:11.5px;color:#a1a1aa">{{ row.job.trigger }} &middot; started {{ row.job.startedAt | date:'HH:mm' }}</div>
              </div>
            </div>
            <div>
              <arius-layered-bar [height]="8" [kind]="row.kind" [scanned]="row.scanned" [middle]="row.middle" [top]="row.top" />
              <div style="font-size:12px;color:#71717a;margin-top:6px">{{ row.phase }}</div>
            </div>
            <div>
              <span data-testid="job-status" style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px"
                    [style.color]="row.meta.color" [style.background]="row.meta.bg" [style.border]="row.meta.border">
                <span style="width:7px;height:7px;border-radius:999px" [style.background]="row.meta.dot" [style.animation]="row.meta.pulse ? 'ar-pulse 1.4s infinite' : 'none'"></span>
                {{ row.meta.label }}
              </span>
              <div style="font-size:11.5px;color:#a1a1aa;margin-top:5px">{{ row.eta }}</div>
            </div>
            <a data-testid="job-reattach" [routerLink]="['/jobs', row.job.id]"
               style="justify-self:end;font-size:12.5px;font-weight:600;color:#3b82f6;text-decoration:none">Detail &rsaquo;</a>
          </div>
        } @empty {
          <div style="padding:20px;text-align:center;color:#a1a1aa;font-size:13px">No active jobs.</div>
        }
      </div>

      <!-- Scheduled & history -->
      <div style="margin-top:22px;font-size:11.5px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">Scheduled &amp; history</div>
      <div data-testid="jobs-history" style="margin-top:8px;border:1px solid #ececef;border-radius:13px;overflow:hidden">
        @for (row of historyRows(); track row.job.id; let first = $first) {
          <div data-testid="job-row" style="display:grid;grid-template-columns:260px 1fr 190px 110px;align-items:center;gap:18px;padding:13px 20px;opacity:.75"
               [style.borderTop]="first ? 'none' : '1px solid #f6f6f7'">
            <div style="display:flex;align-items:center;gap:12px">
              <div style="width:36px;height:36px;border-radius:10px;display:flex;align-items:center;justify-content:center"
                   [style.background]="row.job.kind === 'restore' ? '#f5f3ff' : '#eff6ff'" [style.color]="row.job.kind === 'restore' ? '#6d28d9' : '#3b82f6'">
                <i class="ki-filled" [class.ki-cloud-download]="row.job.kind === 'restore'" [class.ki-cloud-add]="row.job.kind !== 'restore'" style="font-size:16px"></i>
              </div>
              <div>
                <div style="font-size:13.5px;font-weight:600;color:#27272a">{{ row.kindLabel }} &middot; {{ row.job.repo }}</div>
                <div style="font-size:11.5px;color:#a1a1aa">{{ row.job.trigger }} &middot; ran {{ row.job.finishedAt | date:'dd MMM HH:mm' }}</div>
              </div>
            </div>
            <div style="font-size:12.5px;color:#71717a">{{ row.summary }}</div>
            <div>
              <span data-testid="job-status" style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px"
                    [style.color]="row.meta.color" [style.background]="row.meta.bg">
                <i class="ki-filled {{ row.meta.icon }}" style="font-size:12px"></i>{{ row.meta.label }}
              </span>
            </div>
            <a [routerLink]="row.job.trigger === 'schedule' ? ['/repos', row.job.repoId] : ['/jobs', row.job.id]"
               style="justify-self:end;font-size:12.5px;font-weight:600;color:#3b82f6;text-decoration:none">{{ row.actionLabel }}</a>
          </div>
        } @empty {
          <div style="padding:20px;text-align:center;color:#a1a1aa;font-size:13px">No history yet.</div>
        }
      </div>
    } @else {
      <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
    }
  `,
  styles: [`@keyframes ar-pulse { 50% { opacity:.45 } }`],
})
export class JobsComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  private readonly jobsData = signal<JobDto[] | undefined>(undefined);
  protected readonly jobs = this.jobsData.asReadonly();
  private reload(): void { this.api.getJobs().subscribe(list => this.jobsData.set(list)); }

  protected readonly needsAttention = computed(() => this.jobs()?.filter(j => j.status === 'awaiting-cost') ?? []);
  // `rehydrating` (auto-resume state isn't visible on the list DTO) lives in Active, not here.
  protected readonly running = computed(() => this.jobs()?.filter(j => isNonTerminal(j.status) && j.status !== 'awaiting-cost') ?? []);
  protected readonly history = computed(() => this.jobs()?.filter(j => !isNonTerminal(j.status)) ?? []);

  protected readonly runningCount = computed(() => this.jobs()?.filter(j => j.status === 'running').length ?? 0);
  protected readonly waitingCount = computed(() => this.jobs()?.filter(j => j.status === 'awaiting-cost' || j.status === 'rehydrating').length ?? 0);

  /** Enabled-schedule count across every repository. Two small REST calls fan out from a list that's already tiny (repos), so a live count is cheap enough to compute directly. */
  protected readonly scheduledCount = toSignal(
    this.api.listRepositories().pipe(
      switchMap(repos => repos.length
        ? forkJoin(repos.map(r => this.api.getSchedules(r.id).pipe(catchError(() => of([] as ScheduleDto[])))))
        : of([] as ScheduleDto[][])),
      map(lists => lists.reduce((n, list) => n + list.filter(s => s.enabled).length, 0)),
      catchError(() => of(0)),
    ),
    { initialValue: 0 },
  );

  /** Live snapshots for Active rows, keyed by jobId — populated by attaching to each row's SignalR job group. */
  private readonly snapshots = signal<Record<string, JobSnapshot>>({});
  private readonly jobSubs = new Map<string, Subscription>();
  private readonly doneSubs = new Map<string, Subscription>();

  protected readonly activeRows = computed<ActiveRow[]>(() => this.running().map(job => {
    const kind: 'archive' | 'restore' = job.kind === 'restore' ? 'restore' : 'archive';
    const s = this.snapshots()[job.id];
    const layers = s ? (kind === 'restore' ? restoreBarLayers(s) : archiveBarLayers(s))
                     : { scanned: kind === 'restore' ? 100 : 0, middle: 0, top: 0 };
    const { scanned, middle, top } = layers;
    const phase = s ? phaseSentence(s, kind) : (job.detail ?? (kind === 'restore' ? 'Resolving snapshot…' : 'Scanning…'));
    const eta = job.status === 'rehydrating' ? 'checked periodically' : formatEta(s?.etaSeconds ?? null);
    return { job, kind, scanned, middle, top, phase, eta, meta: statusMeta(job.status) };
  }));

  protected readonly historyRows = computed<HistoryRow[]>(() => this.history().map(job => {
    const outcome = parseOutcome(job.outcome);
    return {
      job,
      kindLabel: this.kindLabel(job),
      summary: this.outcomeSummary(job, outcome),
      meta: statusMeta(job.status),
      actionLabel: job.trigger === 'schedule' ? 'Edit ›' : 'Detail ›',
    };
  }));

  constructor() {
    this.reload();
    // Attach (join the SignalR job group + seed the current snapshot) once per newly-seen active row,
    // then rely on jobProgress() deltas for the live mini-bar. Cleaned up in ngOnDestroy.
    effect(() => {
      for (const job of this.running()) {
        if (this.jobSubs.has(job.id)) continue;
        const sub = this.realtime.jobProgress(job.id).subscribe(snap => this.snapshots.update(m => ({ ...m, [job.id]: snap })));
        const doneSub = this.realtime.jobDone(job.id).subscribe(() => { this.releaseRow(job.id); this.reload(); });   // finished → tear down its live wiring, then re-fetch so it leaves Active + chips update
        this.jobSubs.set(job.id, sub);
        this.doneSubs.set(job.id, doneSub);
        void this.realtime.attachToJob(job.id)
          .then(state => { if (state) this.snapshots.update(m => ({ ...m, [job.id]: state.snapshot })); })
          .catch(() => {});
      }
    });
  }

  /** Tear down one finished job's live wiring: its progress + done subscriptions, its client-side SignalR group
   *  membership, and its cached snapshot. Without this, every job that starts and finishes while this page stays
   *  mounted leaks a subscription + a group attachment that only ngOnDestroy would ever reclaim. */
  private releaseRow(id: string): void {
    this.jobSubs.get(id)?.unsubscribe(); this.jobSubs.delete(id);
    this.doneSubs.get(id)?.unsubscribe(); this.doneSubs.delete(id);
    void this.realtime.detachFromJob(id);
    this.snapshots.update(m => { const { [id]: _removed, ...rest } = m; return rest; });
  }

  ngOnDestroy(): void {
    for (const [id, sub] of this.jobSubs) {
      sub.unsubscribe();
      void this.realtime.detachFromJob(id);
    }
    this.jobSubs.clear();
    for (const sub of this.doneSubs.values()) sub.unsubscribe();
    this.doneSubs.clear();
  }

  protected kindLabel(job: JobDto): string {
    return job.kind === 'restore' ? 'Restore' : 'Archive';
  }

  private outcomeSummary(job: JobDto, o: JobOutcome | null): string {
    if (!o) return job.detail ?? (statusMeta(job.status).label);
    const duration = o.durationSeconds != null ? formatDuration(o.durationSeconds) : null;
    const parts = job.kind === 'restore'
      ? [
          o.filesRestored != null ? `${formatCount(o.filesRestored)} files` : null,
          o.downloadedBytes != null ? formatBytes(o.downloadedBytes) : null,
          duration,
        ]
      : [
          o.fileCount != null ? `${formatCount(o.fileCount)} files` : null,
          o.uploadedBytes != null ? `${formatBytes(o.uploadedBytes)} uploaded` : null,
          o.dedupedBytes != null ? `${formatBytes(o.dedupedBytes)} deduped` : null,
          o.snapshotTimestamp != null ? `snapshot ${formatTimestamp(o.snapshotTimestamp)}` : null,
          duration,
        ];
    const joined = parts.filter((p): p is string => p != null).join(' · ');
    return joined || (job.detail ?? statusMeta(job.status).label);
  }
}
