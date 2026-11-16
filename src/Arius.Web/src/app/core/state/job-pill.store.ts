import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { JobSnapshot, PendingDownloadDto, ResumeInfo, isNonTerminal } from '../api/api-models';
import { Subscription } from 'rxjs';
import { JobKind, toJobKind } from '../../shared/job-format';
import { DownloadSummary, summarize } from '../../features/jobs/download-files';

/**
 * Repo-scoped floating-pill state. At most one active job per repo, so the pill adapts
 * to that one job. Owned by RepoDetailComponent — discovers the repo's active job on mount, accepts a
 * direct hand-off from the drawer's Start, and re-attaches on revisit. "Dismiss" is view-only.
 *
 * Downloads add a second mode: with no active job, a finished download that still has files to fetch is
 * shown as `download-ready` (no live snapshot). Dismissing that pill is remembered per browser and job.
 */
@Injectable({ providedIn: 'root' })
export class JobPillStore {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private subs: Subscription[] = [];
  private currentRepoId = 0;

  readonly jobId = signal<string | null>(null);
  readonly kind = signal<JobKind>('archive');
  readonly status = signal<string>('running');
  readonly snapshot = signal<JobSnapshot | null>(null);
  /** `progress`: the live job. `download-ready`: a finished download with files left, no snapshot. */
  readonly mode = signal<'progress' | 'download-ready'>('progress');
  readonly download = signal<DownloadSummary | null>(null);
  readonly resume = signal<ResumeInfo | null>(null);
  private lastFilesRestored = -1;
  private readonly dismissed = signal(false);
  readonly visible = computed(() => this.jobId() !== null && !this.dismissed());

  /** On entering a repo: find its active job (if any) and attach. */
  discover(repoId: number): void {
    if (repoId === this.currentRepoId) {         // revisit / input re-fire for the same repo
      if (!this.jobId()) this.pollActive(repoId); // keep a live pill; re-poll only if we're not already showing one
      return;
    }
    this.detach();                                // switching repos: drop the old repo's job + its SignalR attachment
    this.currentRepoId = repoId;
    this.pollActive(repoId);
  }

  private pollActive(repoId: number): void {
    this.api.getJobs({ repositoryId: repoId, status: 'active' }).subscribe(jobs => {
      if (this.currentRepoId !== repoId) return;  // navigated away before the request resolved
      const job = jobs[0];
      if (job) this.attach(job.id, toJobKind(job.kind), job.status);
      else this.discoverPendingDownload(repoId);
    });
  }

  /** No active job: offer the latest finished download that still has files to fetch, unless dismissed. */
  private discoverPendingDownload(repoId: number, then?: () => void): void {
    this.api.getPendingDownload(repoId).subscribe(p => {
      if (this.currentRepoId !== repoId || (this.jobId() && !then)) return;
      if (p && !this.isDismissed(p.jobId)) this.showDownloadReady(p);
      else then?.();
    });
  }

  private showDownloadReady(p: PendingDownloadDto): void {
    if (this.jobId() !== p.jobId) { this.teardown(); this.dismissed.set(false); }
    this.jobId.set(p.jobId);
    this.kind.set('download');
    this.mode.set('download-ready');
    this.snapshot.set(null);
    // readyBytes is not reported by the pending endpoint and the pill only shows the remaining size.
    this.download.set({ total: p.totalFiles, ready: p.readyFiles, readyBytes: 0, downloaded: p.readyFiles - p.remainingFiles,
      remaining: p.remainingFiles, remainingBytes: p.remainingBytes, pending: p.totalFiles - p.readyFiles });
  }

  private isDismissed(jobId: string): boolean {
    try { return localStorage.getItem(`arius.pill.dismissed.${jobId}`) === '1'; } catch { return false; }
  }

  /** Direct hand-off from the drawer's Start (jobId known immediately). */
  show(jobId: string, kind: JobKind): void {
    this.dismissed.set(false);
    this.attach(jobId, kind, 'running');
  }

  /** Client-only hide (does not cancel the job). */
  dismiss(): void {
    this.dismissed.set(true);
    const id = this.jobId();
    if (id && this.mode() === 'download-ready') {
      try { localStorage.setItem(`arius.pill.dismissed.${id}`, '1'); } catch { /* storage unavailable: dismiss only lasts for this page */ }
    }
  }

  /** Drop the pill entirely (e.g. leaving the repo). */
  detach(): void {
    const id = this.jobId();
    if (id) void this.realtime.detachFromJob(id);
    this.teardown();
    this.jobId.set(null);
    this.snapshot.set(null);
    this.mode.set('progress');
    this.download.set(null);
    this.resume.set(null);
    this.currentRepoId = 0;
  }

  private attach(jobId: string, kind: JobKind, status: string): void {
    if (this.jobId() === jobId) return;
    this.dismissed.set(false);   // a genuinely new job (new repo or new run) must not stay hidden by a prior dismiss
    this.teardown();
    this.jobId.set(jobId);
    this.kind.set(kind);
    this.status.set(status);
    this.mode.set('progress');
    this.download.set(null);
    this.resume.set(null);
    this.lastFilesRestored = -1;
    if (kind === 'download') this.loadDownload(jobId);
    // jobDone drives the auto-hide — but a job that fails synchronously server-side broadcasts its terminal Done
    // before startArchive/startRestore even returns the jobId, i.e. before we subscribe below (done$ doesn't replay),
    // so that Done would be missed and the pill would hang on "Running" forever. AttachToJob closes the gap: by the
    // time it resolves the row is already terminal — or was never inserted (null) because the job failed before the
    // insert — either of which we treat as done.
    void this.realtime.attachToJob(jobId).then(state => {
      if (this.jobId() !== jobId) return;
      if (!state) { this.status.set('failed'); this.finish(jobId); return; }
      this.snapshot.set(state.snapshot); this.status.set(state.status); this.resume.set(state.resume);
      if (!isNonTerminal(state.status)) this.finish(jobId);
    }).catch(() => {});
    this.subs.push(this.realtime.jobProgress(jobId).subscribe(s => {
      this.snapshot.set(s);
      if (kind === 'download' && s.filesRestored !== this.lastFilesRestored) { this.lastFilesRestored = s.filesRestored; this.loadDownload(jobId); }
    }));
    this.subs.push(this.realtime.jobDone(jobId).subscribe(d => { this.status.set(d.status); this.finish(jobId); }));
  }

  private loadDownload(jobId: string): void {
    this.api.getDownload(jobId).subscribe({
      next: d => { if (this.jobId() === jobId && this.mode() === 'progress') this.download.set(summarize(d.files)); },
      error: () => {},
    });
  }

  /** A terminal job auto-hides the pill shortly after (the detail page/overview carry the history).
   *  A finished download first re-runs pending discovery: with files left, the pill becomes `download-ready` instead. */
  private finish(jobId: string): void {
    const autoHide = () => setTimeout(() => {
      if (this.jobId() === jobId && this.mode() === 'progress') { this.jobId.set(null); this.snapshot.set(null); }
    }, 4000);
    if (this.kind() === 'download' && this.currentRepoId) this.discoverPendingDownload(this.currentRepoId, autoHide);
    else autoHide();
  }

  private teardown(): void { this.subs.forEach(s => s.unsubscribe()); this.subs = []; }
}
