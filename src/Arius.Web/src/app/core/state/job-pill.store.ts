import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { JobSnapshot, isNonTerminal } from '../api/api-models';
import { Subscription } from 'rxjs';

/**
 * Repo-scoped floating-pill state. At most one active job per repo (Plan-2 guard), so the pill adapts
 * to that one job. Owned by RepoDetailComponent — discovers the repo's active job on mount, accepts a
 * direct hand-off from the drawer's Start, and re-attaches on revisit. "Dismiss" is view-only.
 */
@Injectable({ providedIn: 'root' })
export class JobPillStore {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private subs: Subscription[] = [];
  private currentRepoId = 0;

  readonly jobId = signal<string | null>(null);
  readonly kind = signal<'archive' | 'restore'>('archive');
  readonly status = signal<string>('running');
  readonly snapshot = signal<JobSnapshot | null>(null);
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
      if (job) this.attach(job.id, job.kind === 'restore' ? 'restore' : 'archive', job.status);
    });
  }

  /** Direct hand-off from the drawer's Start (jobId known immediately). */
  show(jobId: string, kind: 'archive' | 'restore'): void {
    this.dismissed.set(false);
    this.attach(jobId, kind, 'running');
  }

  /** Client-only hide (does not cancel the job). */
  dismiss(): void { this.dismissed.set(true); }

  /** Drop the pill entirely (e.g. leaving the repo). */
  detach(): void {
    const id = this.jobId();
    if (id) void this.realtime.detachFromJob(id);
    this.teardown();
    this.jobId.set(null);
    this.snapshot.set(null);
    this.currentRepoId = 0;
  }

  private attach(jobId: string, kind: 'archive' | 'restore', status: string): void {
    if (this.jobId() === jobId) return;
    this.dismissed.set(false);   // a genuinely new job (new repo or new run) must not stay hidden by a prior dismiss
    this.teardown();
    this.jobId.set(jobId);
    this.kind.set(kind);
    this.status.set(status);
    // jobDone drives the auto-hide — but a job that fails synchronously server-side broadcasts its terminal Done
    // before startArchive/startRestore even returns the jobId, i.e. before we subscribe below (done$ doesn't replay),
    // so that Done would be missed and the pill would hang on "Running" forever. AttachToJob closes the gap: by the
    // time it resolves the row is already terminal — or was never inserted (null) because the job failed before the
    // insert — either of which we treat as done (review #6).
    void this.realtime.attachToJob(jobId).then(state => {
      if (this.jobId() !== jobId) return;
      if (!state) { this.status.set('failed'); this.finish(jobId); return; }
      this.snapshot.set(state.snapshot); this.status.set(state.status);
      if (!isNonTerminal(state.status)) this.finish(jobId);
    }).catch(() => {});
    this.subs.push(this.realtime.jobProgress(jobId).subscribe(s => this.snapshot.set(s)));
    this.subs.push(this.realtime.jobDone(jobId).subscribe(d => { this.status.set(d.status); this.finish(jobId); }));
  }

  /** A terminal job auto-hides the pill shortly after (the detail page/overview carry the history). */
  private finish(jobId: string): void {
    setTimeout(() => { if (this.jobId() === jobId) { this.jobId.set(null); this.snapshot.set(null); } }, 4000);
  }

  private teardown(): void { this.subs.forEach(s => s.unsubscribe()); this.subs = []; }
}
