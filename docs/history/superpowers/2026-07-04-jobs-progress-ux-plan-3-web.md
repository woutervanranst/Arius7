# Jobs/Progress UX — Plan 3: Angular Web rework

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the terminal-style live console + count-based bar with the approved handoff design — a byte-weighted layered progress bar, a floating repo-scoped pill, a reattachable `/jobs/:id` detail page, and a redesigned Jobs overview — all driven by the absolute-state realtime protocol and REST surface that Plans 1–2 built.

**Architecture:** The Angular client (standalone components, signals, OnPush) switches from the legacy `Log`/`{pct,stats}` stream to the Plan-2 protocol: `AttachToJob(jobId)` returns a full `JobSnapshot` then deltas (jobId-tagged, latest-wins), with reconnect re-attach. A shared `LayeredBarComponent` renders the three overlapping byte-fills. A repo-scoped `JobPillStore` + `JobPillComponent` float the pill inside the repo detail shell. A single `JobDetailComponent` drives both archive and restore at `/jobs/:id`. The Jobs overview becomes a sectioned list (Needs-attention / Active / Scheduled-&-history). The live console and the drawer's stream/cost UI are deleted; the drawer becomes form-only and hands the jobId to the pill on Start.

**Tech stack:** Angular 21 (standalone, signals + computed, `ChangeDetectionStrategy.OnPush`, inline templates), `@microsoft/signalr`, Tailwind utility classes over Metronic, KeenIcons (`ki-filled`), Playwright e2e.

**Design source (authoritative, vendored in-repo):**
- `docs/history/superpowers/handoff-jobs-progress-ux/README.md` — pixel-precise spec (tokens, layouts, copy). **Implement turns 4a (overview), 3a (archive detail), 3b (restore detail), and the 1c pill.**
- `docs/history/superpowers/handoff-jobs-progress-ux/Progress UX Options.dc.html` — the design canvas (exact markup for the layered bar, pill, stage rows, KPI tiles).
- Design spec: `docs/history/superpowers/2026-07-04-jobs-progress-ux-design.md` (§5, §10, §12, §13, §15, §18).

**Predecessors:** Plan 1 (`…-plan-1-foundation.md`) + Plan 2 (`…-plan-2-lifecycle.md`), both complete/green. This plan consumes their Api surface.

## Global Constraints

- **Match the existing Angular idiom exactly.** Standalone components, `ChangeDetectionStrategy.OnPush`, signals + `computed`, inline templates + inline `styles: [...]`, `toSignal`/`toObservable`, `inject()`. The codebase styles with **inline `style="..."` hex tokens** and a few shared classes (`ar-card`, `ar-pill`, `ar-btn-primary`, `ar-btn-outline`, `ar-icon-btn`, `ar-chip`, `ar-heading`, `ar-seg`, `ar-toggle`) — follow that, do not introduce a CSS framework or a global stylesheet.
- **`data-testid` on every element an e2e spec asserts.** The suite selects by `getByTestId`. Preserve existing testids where behavior moves, and add new ones per this plan.
- **Design fidelity is HIGH.** Use the exact tokens/copy/px from the README §Design Tokens. Archive palette `#dbeafe → #93c5fd → #2563eb` (accents `#3b82f6`/`#1d4ed8`, chip bg `#eff6ff`); restore palette `#ede9fe → #c4b5fd → #7c3aed` (accents `#6d28d9`, chip bg `#f5f3ff`); amber `#b45309`/`#fffbeb`/`#fde68a`; green `#22c55e`/`#15803d`; red `#dc2626`. Track `#eef0f3`. Detail bar 14px, overview mini-bar 8px, all `border-radius:999px`.
- **Absolute-state, latest-wins.** Every `Progress` message and the `AttachToJob` snapshot carry the full `JobSnapshot`; the client replaces its state (never accumulates). Every message is jobId-tagged so a client attached to several jobs routes correctly.
- **Reattach.** `AttachToJob(jobId)` returns the current snapshot in one round trip (no gap); the client re-issues `AttachToJob` for every attached jobId in `onreconnected` (withAutomaticReconnect gets a new connectionId and silently loses group membership).
- **No per-file updates. Pulse is the only animation** (`@keyframes ar-pulse { 50% { opacity:.45 } }`, 1.4s infinite, on running status dots + running stage bullets). Bar fills use `transition: width .3s`.
- **Kill the console.** Delete `live-console.component.ts` and every usage. Remove `RealtimeService.log$`.
- **Status vocabulary (from the Api):** non-terminal `running` | `awaiting-cost` | `rehydrating`; terminal `completed` | `failed` | `cancelled` | `interrupted`. **`queued` and `scheduled`-as-a-job-status are gone.** A *schedule* is a separate entity — the purple heading chip counts enabled schedules, not jobs.
- **Dropped from the handoff (recorded product decisions, design §18):** the `showFooterNotes` footer notes (removed entirely — do not build them); "16 parallel workers" / "took 32 s last run" / "+~30 s snapshot" KPI sublabels (the pulsing bullet signals "busy"); snapshot labels are **timestamps**, not ordinals ("v12").
- **Deferred by Plan 2 (do NOT assume present):** the cost estimate is **not** replayed on attach (`JobAttachState.Cost` is always null) — a reconnecting client to an `awaiting-cost` job shows the "Review cost ›" affordance from the *status* and fetches the live estimate only while it is pushed on the socket during the same session. Render the cost modal from a live `CostEstimate` push (captured per jobId); if the user reloads the detail page of an `awaiting-cost` job, show a "Review cost" prompt whose button re-issues nothing (the estimate arrives on the next push) — see Task 4 for the exact fallback.
- **Testing:** the executable gate is **`npx ng build`** (Angular's strict template + TypeScript typecheck catches binding/type/route errors). Playwright e2e specs (`e2e/specs/*.spec.ts`) run against a **live backend** (`npm run e2e`, needs a running Api on :5080 + `ARIUS_E2E_*`), so they are **authored/updated per task and run in CI**, not in the local TDD loop. Each task ends with a green `ng build` and updated/added specs. **Environment note:** if `node`/`ng` cannot run in the execution environment, the per-task gate falls back to a strict TypeScript/template read-through by the implementer + reviewer, and `ng build` + `npm run e2e` are run by the human/CI before merge — call this out explicitly in each report.
- **Do not touch the Api or Core.** This plan is `src/Arius.Web`-only. The Plan-2 hub methods and REST endpoints are fixed contracts.

---

## Api surface this plan consumes (fixed — from Plans 1–2)

**Hub (`/hubs/arius`), invoke:**
- `StartArchive(repositoryId, tier, removeLocal, writePointers, fastHash) → jobId` (unchanged; guard-gated — throws `HubException` "A job is already running…" if the repo is busy).
- `StartRestore(repositoryId, version, targetPaths[], overwrite, noPointers) → jobId` (same guard).
- `AttachToJob(jobId) → JobAttachState | null` · `DetachFromJob(jobId)`
- `CancelJob(jobId)` · `ApproveRestore(jobId, priority)` · `DeclineRestore(jobId)` · `Approve(jobId, priority)` (legacy alias → ApproveRestore) · `SetAutoResume(jobId, autoResume)` · `ResumeRestore(jobId)`

**Hub, on (server→client), all jobId-tagged:**
- `Progress` → `JobSnapshot` (absolute state; also the `AttachToJob` snapshot shape)
- `CostEstimate` → `CostEstimateDto` (jobId + wait hours)
- `Done` → `{ jobId, status, summary, outcome }` (`outcome` = serialized `JobOutcome` JSON string or null)
- `Log` → **still emitted by the Api but IGNORED by this plan** (console removed; do not subscribe)

**REST (`/api`):**
- `GET /jobs?repositoryId={id}&status={active|terminal|<exact>}` → `JobDto[]` (`active` = the 3 non-terminal statuses)
- `GET /jobs/{id}` → `JobDetailDto`
- `GET /jobs/{id}/warnings` → `JobWarningsDto`
- `GET /repos/{id}/schedules` → `ScheduleDto[]` (unchanged; the purple chip counts `enabled`)

**Exact server DTO shapes** (camelCase over the wire; `JobSnapshot` fields are `JobSnapshot.cs`; `CostEstimateDto`/`JobAttachState`/`JobDetailDto`/`JobWarningsDto` are `JobDetailDtos.cs`; `JobOutcome` is `JobSnapshot.cs`):

```
JobSnapshot { jobId, phase, totalBytes, totalNewBytes, scannedBytes, hashedBytes, uploadedBytes,
              dedupedBytes, dedupedFiles, etaSeconds|null, throughputBytesPerSec, pct, warningCount,
              stats: Record<string,string>,
              restoreTotalFiles, filesRestored, restoreTotalBytes, bytesRestored,
              chunksAvailable, chunksRehydrated, chunksNeedingRehydration, chunksPending }
CostEstimateDto { jobId, chunksAvailable, chunksNeedingRehydration, bytesNeedingRehydration,
                  downloadBytes, totalStandard, totalHigh, standardWaitHours, highWaitHours }
DoneMsg (on 'Done') { jobId, status, summary, outcome: string|null }   // outcome = JSON of JobOutcome
JobOutcome { fileCount|null, uploadedBytes|null, dedupedBytes|null, filesRestored|null,
             downloadedBytes|null, snapshotTimestamp|null, durationSeconds|null }
JobAttachState { status, snapshot: JobSnapshot, cost: CostEstimateDto|null, warningCount }
JobDto { id, repoId, repo, kind, trigger, status, pct, detail|null, startedAt|null, finishedAt|null, outcome: string|null }
JobDetailDto { id, repoId, repo, kind, trigger, status, pct, detail|null, startedAt|null, finishedAt|null,
               outcome: string|null, snapshot: JobSnapshot|null, warningCount }
JobWarningsDto { count, lines: string[], truncated }
```

---

## File structure

**Create:**
- `src/app/shared/layered-bar/layered-bar.component.ts` — the 3-fill byte-weighted track + optional legend (archive/restore palette).
- `src/app/shared/job-format.ts` — `formatEta`, `formatDuration`, `formatThroughput`, `hydratedByLabel`, `statusMeta`, `phaseSentence` helpers.
- `src/app/core/state/job-pill.store.ts` — repo-scoped pill state (attach/detach, discovery, live snapshot, dismiss).
- `src/app/features/pill/job-pill.component.ts` — the floating pill.
- `src/app/features/jobs/job-detail.component.ts` — `/jobs/:id` (archive + restore).

**Modify:**
- `src/app/core/api/api-models.ts` — replace `ProgressMsg`/`CostEstimateMsg`/`DoneMsg`; extend `JobDto`; add `JobSnapshot`, `JobOutcome`, `JobAttachState`, `JobDetailDto`, `JobWarningsDto`; `JobStatus` union + status constants.
- `src/app/core/api/api.service.ts` — `getJob`, `getJobWarnings`, `getJobs(filters)`.
- `src/app/core/api/realtime.service.ts` — jobId-tagged `jobStream(jobId)`; `attachToJob`/`detachFromJob` + reconnect re-attach; `cancelJob`, `approveRestore`, `declineRestore`, `setAutoResume`, `resumeRestore`; **remove `log$`**.
- `src/app/core/state/drawer.store.ts` — strip stream/console/cost → form-only; on Start hand the jobId to `JobPillStore`.
- `src/app/features/drawer/archive-restore-drawer.component.ts` — form-only (remove stream view, cost modal, console import).
- `src/app/features/jobs/jobs.component.ts` — overview redesign (Needs-attention / Active / Scheduled-&-history); remove console.
- `src/app/features/repo/repo-detail.component.ts` — mount `<arius-job-pill [repoId]>`; hand the started jobId to the pill.
- `src/app/app.routes.ts` — add `jobs/:id`.

**Delete:**
- `src/app/shared/live-console/live-console.component.ts`.

**e2e (update):** `archive.spec.ts`, `restore.spec.ts`, `cost-approval.spec.ts`, `jobs.spec.ts`, `restore-roundtrip.spec.ts` — testids move from the drawer stream/cost UI to the pill + `/jobs/:id`. **(Add):** `pill.spec.ts`, `job-detail.spec.ts`.

---

## Task 1: Client contracts + REST client + realtime protocol (additive)

**Files:**
- Modify: `src/app/core/api/api-models.ts`, `src/app/core/api/api.service.ts`, `src/app/core/api/realtime.service.ts`

**Interfaces:**
- Produces: the DTO interfaces above; `ApiService.getJob(id)`, `getJobWarnings(id)`, `getJobs(opts?)`; `RealtimeService.jobStream(jobId)`, `attachToJob(jobId)`, `detachFromJob(jobId)`, `cancelJob(jobId)`, `approveRestore(jobId, priority)`, `declineRestore(jobId)`, `setAutoResume(jobId, on)`, `resumeRestore(jobId)`.
- **Keep** `RealtimeService.progress$`/`cost$`/`done$` and `startArchive`/`startRestore` this task (the legacy drawer still consumes them; they are removed in Task 6). **This task keeps `log$` too** — it is removed in Task 6 together with its last consumer. So this task is purely additive → `ng build` stays green.

**Context:** This lays the typed foundation. Nothing else changes yet, so the app still runs the legacy path; later tasks consume the new surface and Task 6 removes the legacy one.

- [ ] **Step 1: Replace the stale job DTOs in `api-models.ts`**

Replace the `ProgressMsg`, `CostEstimateMsg`, `DoneMsg`, and `JobDto` interfaces (lines ~87-117) with the full set:

```typescript
// ── Jobs: absolute-state realtime + REST (Plan 2 protocol) ────────────────────

export const NON_TERMINAL_STATUSES = ['running', 'awaiting-cost', 'rehydrating'] as const;
export type JobStatus = 'running' | 'awaiting-cost' | 'rehydrating' | 'completed' | 'failed' | 'cancelled' | 'interrupted';
export const isNonTerminal = (s: string): boolean => (NON_TERMINAL_STATUSES as readonly string[]).includes(s);

/** Absolute-state progress snapshot — the `Progress` message payload AND the `AttachToJob` snapshot. Apply latest-wins. */
export interface JobSnapshot {
  jobId: string;
  phase: string;
  totalBytes: number;
  totalNewBytes: number;
  scannedBytes: number;
  hashedBytes: number;
  uploadedBytes: number;
  dedupedBytes: number;
  dedupedFiles: number;
  etaSeconds: number | null;
  throughputBytesPerSec: number;
  pct: number;
  warningCount: number;
  stats: Record<string, string>;
  // restore layers
  restoreTotalFiles: number;
  filesRestored: number;
  restoreTotalBytes: number;
  bytesRestored: number;
  chunksAvailable: number;
  chunksRehydrated: number;
  chunksNeedingRehydration: number;
  chunksPending: number;
}

export interface CostEstimateMsg {
  jobId: string;
  chunksAvailable: number;
  chunksNeedingRehydration: number;
  bytesNeedingRehydration: number;
  downloadBytes: number;
  totalStandard: number;
  totalHigh: number;
  standardWaitHours: number;
  highWaitHours: number;
}

export interface DoneMsg {
  jobId: string;
  status: string;
  summary: string;
  outcome: string | null;   // JSON of JobOutcome, or null
}

export interface JobOutcome {
  fileCount: number | null;
  uploadedBytes: number | null;
  dedupedBytes: number | null;
  filesRestored: number | null;
  downloadedBytes: number | null;
  snapshotTimestamp: string | null;
  durationSeconds: number | null;
}

export interface JobAttachState {
  status: string;
  snapshot: JobSnapshot;
  cost: CostEstimateMsg | null;
  warningCount: number;
}

export interface JobDto {
  id: string;
  repoId: number;
  repo: string;
  kind: string;     // archive | restore
  trigger: string;  // one-off | schedule
  status: string;   // JobStatus
  pct: number;
  detail: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  outcome: string | null;   // JSON of JobOutcome (history rows), or null
}

export interface JobDetailDto {
  id: string;
  repoId: number;
  repo: string;
  kind: string;
  trigger: string;
  status: string;
  pct: number;
  detail: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  outcome: string | null;
  snapshot: JobSnapshot | null;
  warningCount: number;
}

export interface JobWarningsDto {
  count: number;
  lines: string[];
  truncated: boolean;
}
```

Delete the now-unused `LogLine` interface **only in Task 6** (the legacy console + `log$` still use it until then — leave it here).

- [ ] **Step 2: Add the REST methods to `api.service.ts`**

Update the import to include `JobDetailDto, JobWarningsDto`, then add:

```typescript
  /** Jobs list, optionally filtered by repository and/or status ('active' = the non-terminal set). */
  getJobs(opts?: { repositoryId?: number; status?: 'active' | 'terminal' | string }): Observable<JobDto[]> {
    const params = new URLSearchParams();
    if (opts?.repositoryId != null) params.set('repositoryId', String(opts.repositoryId));
    if (opts?.status) params.set('status', opts.status);
    const query = params.toString();
    return this.http.get<JobDto[]>(`/api/jobs${query ? `?${query}` : ''}`);
  }

  getJob(id: string): Observable<JobDetailDto> {
    return this.http.get<JobDetailDto>(`/api/jobs/${id}`);
  }

  getJobWarnings(id: string): Observable<JobWarningsDto> {
    return this.http.get<JobWarningsDto>(`/api/jobs/${id}/warnings`);
  }
```

(The existing zero-arg `getJobs()` call in `jobs.component.ts` still compiles — `opts` is optional. It is replaced in Task 5.)

- [ ] **Step 3: Extend `realtime.service.ts` — jobId-tagged stream + attach/lifecycle**

Add jobId-routed streams and the new invokes, and re-attach on reconnect. Change the imports to add `JobSnapshot, JobAttachState` and update `CostEstimateMsg, DoneMsg` (already imported). Add these members to the class:

```typescript
  /** jobIds this client is attached to — re-issued on reconnect (withAutomaticReconnect drops group membership). */
  private readonly attached = new Set<string>();

  /** Absolute-state progress, tagged by jobId. Subscribers filter by their own jobId. */
  readonly progress$ = new Subject<JobSnapshot>();     // note: payload is now JobSnapshot (has .jobId)
  readonly cost$ = new Subject<CostEstimateMsg>();      // now jobId-tagged
  readonly done$ = new Subject<DoneMsg>();              // now jobId-tagged

  /** Filtered view of progress$ for one job. */
  jobProgress(jobId: string): Observable<JobSnapshot> {
    return this.progress$.pipe(filter(s => s.jobId === jobId));
  }
  jobCost(jobId: string): Observable<CostEstimateMsg> {
    return this.cost$.pipe(filter(c => c.jobId === jobId));
  }
  jobDone(jobId: string): Observable<DoneMsg> {
    return this.done$.pipe(filter(d => d.jobId === jobId));
  }

  /** Joins the job's group and returns the current snapshot (live or persisted). Tracks it for reconnect re-attach. */
  async attachToJob(jobId: string): Promise<JobAttachState | null> {
    await this.ensureStarted();
    this.attached.add(jobId);
    return this.connection!.invoke<JobAttachState | null>('AttachToJob', jobId);
  }
  async detachFromJob(jobId: string): Promise<void> {
    this.attached.delete(jobId);
    if (this.connection?.state === signalR.HubConnectionState.Connected)
      await this.connection.invoke('DetachFromJob', jobId);
  }

  async cancelJob(jobId: string): Promise<void> { await this.ensureStarted(); await this.connection!.invoke('CancelJob', jobId); }
  async approveRestore(jobId: string, priority: 'standard' | 'high'): Promise<void> { await this.ensureStarted(); await this.connection!.invoke('ApproveRestore', jobId, priority); }
  async declineRestore(jobId: string): Promise<void> { await this.ensureStarted(); await this.connection!.invoke('DeclineRestore', jobId); }
  async setAutoResume(jobId: string, autoResume: boolean): Promise<void> { await this.ensureStarted(); await this.connection!.invoke('SetAutoResume', jobId, autoResume); }
  async resumeRestore(jobId: string): Promise<void> { await this.ensureStarted(); await this.connection!.invoke('ResumeRestore', jobId); }
```

In `ensureStarted`, replace the `Progress`/`CostEstimate`/`Done` handler bindings (keep `Log` for now) with the typed payloads and add the reconnect hook (once, next to `handlersBound`):

```typescript
      this.connection.on('Progress', (m: JobSnapshot) => this.progress$.next(m));
      this.connection.on('CostEstimate', (m: CostEstimateMsg) => this.cost$.next(m));
      this.connection.on('Done', (m: DoneMsg) => this.done$.next(m));
      this.connection.onreconnected(() => { for (const id of this.attached) void this.connection!.invoke('AttachToJob', id); });
```

Add `import { filter } from 'rxjs/operators';` and `import { ..., JobSnapshot, JobAttachState } from './api-models';`. Keep the existing `Log`/`log$`, `startArchive`, `startRestore`, `approve`, `searchAll`, `streamContainers`, `listEntries` methods unchanged.

- [ ] **Step 4: Build gate**

Run: `npx ng build --configuration development`
Expected: clean (0 errors). The legacy `DrawerStore`/`jobs.component` still compile because `log$`/`progress$`/`cost$`/`done$` and `getJobs()` remain. (If `node`/`ng` cannot run in this environment, verify by TypeScript read-through and note it in the report; the human/CI runs `ng build`.)

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Web/src/app/core/api/
git commit -m "feat(web): job DTOs + REST reads + attach/lifecycle realtime protocol (Plan 2 client surface)"
```

---

## Task 2: `LayeredBarComponent` + job-format helpers

**Files:**
- Create: `src/app/shared/layered-bar/layered-bar.component.ts`, `src/app/shared/job-format.ts`

**Interfaces:**
- Produces: `<arius-layered-bar [kind] [height] [scanned] [middle] [top] />` where `scanned`/`middle`/`top` are 0–100 percentages of the same track (each a subset of the previous); `kind: 'archive' | 'restore'` picks the palette. Helpers: `formatEta(seconds|null)`, `formatDuration(seconds)`, `formatThroughput(bytesPerSec)`, `hydratedByLabel(startedAtIso, windowHours)`, `statusMeta(status)`, `phaseSentence(snapshot, kind)`.

**Context:** The layered bar is the design's core idea (README §Screens 2): ONE rounded track with three absolutely-positioned overlapping fills, all in bytes of the same dataset, so the bar never jumps or hangs. Used at 14px on the detail page and 8px in the overview mini-bar.

- [ ] **Step 1: Create `job-format.ts`**

```typescript
import { JobSnapshot } from '../core/api/api-models';

/** "~12 min left" / "estimating…" (null until totalNewBytes is known). */
export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null) return 'estimating…';
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))} sec left`;
  if (seconds < 3600) return `~${Math.round(seconds / 60)} min left`;
  return `~${(seconds / 3600).toFixed(1)} h left`;
}

/** "11 min" / "1.4 h" / "48 s" — elapsed/duration display. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null) return '—';
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

/** "2.4 MB/s". */
export function formatThroughput(bytesPerSec: number | null | undefined): string {
  const b = bytesPerSec ?? 0;
  if (b >= 1e6) return `${(b / 1e6).toFixed(1)} MB/s`;
  if (b >= 1e3) return `${(b / 1e3).toFixed(0)} KB/s`;
  return `${Math.round(b)} B/s`;
}

/** "≈ hydrated by 03:40" from a rehydration start + the priority window (hours). */
export function hydratedByLabel(startedAtIso: string | null, windowHours: number): string {
  if (!startedAtIso) return '';
  const done = new Date(new Date(startedAtIso).getTime() + windowHours * 3600_000);
  return `≈ hydrated by ${done.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}`;
}

export interface StatusMeta { label: string; color: string; bg: string; border: string; dot: string; icon: string; pulse: boolean; }

/** Chip styling per status (README §Screens; amber for waiting, blue running, etc.). */
export function statusMeta(status: string): StatusMeta {
  switch (status) {
    case 'running':      return { label: 'Running',      color: '#1d4ed8', bg: '#eff6ff', border: 'none', dot: '#3b82f6', icon: 'ki-loading',       pulse: true };
    case 'awaiting-cost':return { label: 'Review cost',  color: '#b45309', bg: '#fffbeb', border: '1px solid #fde68a', dot: '#b45309', icon: 'ki-dollar', pulse: false };
    case 'rehydrating':  return { label: 'Rehydrating',  color: '#b45309', bg: '#fffbeb', border: '1px solid #fde68a', dot: '#b45309', icon: 'ki-time',   pulse: true };
    case 'completed':    return { label: 'Completed',    color: '#15803d', bg: '#f0fdf4', border: 'none', dot: '#22c55e', icon: 'ki-check-circle',  pulse: false };
    case 'failed':       return { label: 'Failed',       color: '#dc2626', bg: '#fef2f2', border: 'none', dot: '#dc2626', icon: 'ki-cross-circle',  pulse: false };
    case 'cancelled':    return { label: 'Cancelled',    color: '#71717a', bg: '#f4f4f5', border: 'none', dot: '#a1a1aa', icon: 'ki-cross-circle',  pulse: false };
    case 'interrupted':  return { label: 'Interrupted',  color: '#a16207', bg: '#fefce8', border: 'none', dot: '#a16207', icon: 'ki-time',          pulse: false };
    default:             return { label: status,         color: '#52525b', bg: '#f4f4f5', border: 'none', dot: '#a1a1aa', icon: 'ki-time',          pulse: false };
  }
}

/** One phase sentence for the pill / overview row / detail header, e.g. "Uploading — 1.68 of 3.11 GB · 2.4 MB/s". */
export function phaseSentence(s: JobSnapshot, kind: string): string {
  const gb = (n: number) => (n / 1e9).toFixed(2) + ' GB';
  if (kind === 'restore') {
    if (s.chunksNeedingRehydration > 0 && s.bytesRestored === 0) return `Rehydrating — ${s.chunksNeedingRehydration} chunks from Archive tier`;
    return `Restoring — ${s.filesRestored} of ${s.restoreTotalFiles} files`;
  }
  if (s.totalNewBytes === 0) return 'Scanning & hashing — estimating…';
  return `Uploading — ${gb(s.uploadedBytes)} of ${gb(s.totalNewBytes)}`;
}
```

- [ ] **Step 2: Create `layered-bar.component.ts`**

The exact markup mirrors `Progress UX Options.dc.html` lines 152-155 (archive) / 241-244 (restore): a `position:relative` rounded track with three absolutely-positioned `border-radius:999px` fills.

```typescript
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Byte-weighted layered progress bar (design README §Screens 2). ONE track, three overlapping fills,
 * each a subset of the previous (scanned ⊇ middle ⊇ top), all as % of the same dataset — so it never
 * jumps or hangs. Archive palette blues, restore palette purples.
 */
@Component({
  selector: 'arius-layered-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div data-testid="layered-bar" style="position:relative;border-radius:999px;overflow:hidden"
         [style.height.px]="height()" [style.background]="'#eef0f3'">
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(scanned())" [style.background]="palette()[0]"></div>
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(middle())"  [style.background]="palette()[1]"></div>
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(top())"     [style.background]="palette()[2]"></div>
    </div>
  `,
})
export class LayeredBarComponent {
  readonly kind = input<'archive' | 'restore'>('archive');
  readonly height = input(14);
  readonly scanned = input(0);
  readonly middle = input(0);
  readonly top = input(0);
  protected readonly palette = computed<[string, string, string]>(() =>
    this.kind() === 'restore' ? ['#ede9fe', '#c4b5fd', '#7c3aed'] : ['#dbeafe', '#93c5fd', '#2563eb']);
  protected clamp(n: number): number { return Math.max(0, Math.min(100, n)); }
}
```

- [ ] **Step 3: Build gate + commit**

Run: `npx ng build --configuration development` → clean.

```bash
git add src/Arius.Web/src/app/shared/layered-bar/ src/Arius.Web/src/app/shared/job-format.ts
git commit -m "feat(web): LayeredBarComponent + job-format helpers"
```

---

## Task 3: `JobPillStore` + `JobPillComponent` + mount in the repo shell

**Files:**
- Create: `src/app/core/state/job-pill.store.ts`, `src/app/features/pill/job-pill.component.ts`
- Modify: `src/app/features/repo/repo-detail.component.ts` (mount the pill), `src/app/core/state/drawer.store.ts` (Start → `pill.show(jobId, kind)`)

**Interfaces:**
- Produces: `JobPillStore` with `jobId: Signal<string|null>`, `snapshot: Signal<JobSnapshot|null>`, `status: Signal<string>`, `kind: Signal<'archive'|'restore'>`, `visible: Signal<boolean>`; methods `discover(repoId)`, `show(jobId, kind)`, `dismiss()`, `detach()`. `<arius-job-pill [repoId]="n" />`.
- Consumes: Task 1 (`attachToJob`, `jobProgress`, `jobDone`, `getJobs`), Task 2 (`LayeredBarComponent`, `phaseSentence`, `formatEta`, `formatThroughput`).

**Context:** README §Screens 1 + design §13. With the Plan-2 single-active-job guard there is **0 or 1** active job per repo, so the pill adapts to that one job's state. It appears while viewing the repo, reconnects on revisit, and "Hide pill" is client-only view state (does not cancel). Discovery: `getJobs({repositoryId, status:'active'})` on mount + the drawer hands the jobId on Start.

- [ ] **Step 1: Create `job-pill.store.ts`**

```typescript
import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { JobSnapshot } from '../api/api-models';
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
    if (repoId === this.currentRepoId && this.jobId()) return;   // already tracking this repo's job
    this.currentRepoId = repoId;
    this.api.getJobs({ repositoryId: repoId, status: 'active' }).subscribe(jobs => {
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
    this.teardown();
    this.jobId.set(jobId);
    this.kind.set(kind);
    this.status.set(status);
    void this.realtime.attachToJob(jobId).then(state => {
      if (state && this.jobId() === jobId) { this.snapshot.set(state.snapshot); this.status.set(state.status); }
    });
    this.subs.push(this.realtime.jobProgress(jobId).subscribe(s => this.snapshot.set(s)));
    this.subs.push(this.realtime.jobDone(jobId).subscribe(d => {
      this.status.set(d.status);
      // A terminal job auto-hides the pill shortly after (the detail page/overview carry history).
      setTimeout(() => { if (this.jobId() === jobId) { this.jobId.set(null); this.snapshot.set(null); } }, 4000);
    }));
  }

  private teardown(): void { this.subs.forEach(s => s.unsubscribe()); this.subs = []; }
}
```

- [ ] **Step 2: Create `job-pill.component.ts`** (README §Screens 1 / mockup turn `1c`)

```typescript
import { ChangeDetectionStrategy, Component, computed, inject, input, effect } from '@angular/core';
import { RouterLink } from '@angular/router';
import { JobPillStore } from '../../core/state/job-pill.store';
import { LayeredBarComponent } from '../../shared/layered-bar/layered-bar.component';
import { formatEta, formatThroughput, phaseSentence, statusMeta } from '../../shared/job-format';

/** Floating repo-scoped progress pill (bottom-right of the content area). Dark, 30px SVG ring + two lines + "View job ›". */
@Component({
  selector: 'arius-job-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LayeredBarComponent],
  template: `
    @if (store.visible() && store.snapshot(); as s) {
      <div data-testid="job-pill" style="position:fixed;right:18px;bottom:16px;z-index:50;display:flex;align-items:center;gap:11px;
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
export class JobPillComponent {
  protected readonly store = inject(JobPillStore);
  readonly repoId = input.required<number>();
  protected readonly circumference = 2 * Math.PI * 13;

  constructor() {
    effect(() => { const id = this.repoId(); if (id) this.store.discover(id); });
  }

  protected readonly verb = computed(() => {
    const s = this.store.snapshot()!; const kind = this.store.kind();
    return phaseSentence(s, kind).split(' —')[0] + (kind === 'archive' ? ' photos' : '');   // "Uploading" style verb
  });
  protected line2 = () => {
    const s = this.store.snapshot()!;
    return `${formatEta(s.etaSeconds)} · ${formatThroughput(s.throughputBytesPerSec)}`;
  };
  protected readonly statusMeta = statusMeta;
}
```

(The pill glance-popover from mockup `2a` is optional polish — omit unless trivial; the "View job ›" link is the required navigation.)

- [ ] **Step 3: Mount the pill in `repo-detail.component.ts`**

Import `JobPillComponent`, add it to `imports`, and render it after the tab content (inside the `@if (repo(); as r)` block, at the end):

```html
      <arius-job-pill [repoId]="numericId()" />
```

- [ ] **Step 4: Drawer Start hands the jobId to the pill (`drawer.store.ts`)**

Inject `JobPillStore` and, in `start()`, after obtaining the jobId, hand it over and close the drawer (the pill takes over — README §Interactions "Start → drawer dismisses → pill appears"):

```typescript
  // inside start(), after this.jobId.set(await …startArchive/startRestore):
  const id = this.jobId();
  if (id) { this.pill.show(id, this.type() === 'restore' ? 'restore' : 'archive'); this.type.set(null); }
```

(Add `private readonly pill = inject(JobPillStore);`. Keep the rest of `start()` for now — Task 6 strips the legacy stream state.)

- [ ] **Step 5: Build gate + specs + commit**

Run: `npx ng build --configuration development` → clean.
Add `e2e/specs/pill.spec.ts` (skeleton — asserts the pill appears after Start and "View job ›" navigates to `/jobs/:id`; `@write`-tagged, runs in CI against a live backend):

```typescript
import { test, expect } from '../support/fixtures';
// @write — starts a real archive; scoped like archive.spec.ts.
test('pill appears after Start and opens the job detail page', async ({ page, repo }) => {
  test.setTimeout(120_000);
  await page.goto(`/repos/${repo.repoId}/files`);
  // (open the archive drawer + Start as archive.spec.ts does, then:)
  await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });
  await page.getByTestId('pill-open').click();
  await expect(page).toHaveURL(/\/jobs\//);
});
```

```bash
git add src/Arius.Web/src/app/core/state/job-pill.store.ts src/Arius.Web/src/app/features/pill/ src/Arius.Web/src/app/features/repo/repo-detail.component.ts src/Arius.Web/src/app/core/state/drawer.store.ts src/Arius.Web/e2e/specs/pill.spec.ts
git commit -m "feat(web): repo-scoped floating job pill + drawer→pill hand-off"
```

---

## Task 4: Job detail page `/jobs/:id` (archive + restore)

**Files:**
- Create: `src/app/features/jobs/job-detail.component.ts`
- Modify: `src/app/app.routes.ts` (add `jobs/:id`)

**Interfaces:**
- Consumes: Task 1 (`getJob`, `getJobWarnings`, `attachToJob`, `jobProgress`, `jobCost`, `jobDone`, `cancelJob`, `approveRestore`, `declineRestore`, `setAutoResume`, `resumeRestore`), Task 2 (`LayeredBarComponent`, all job-format helpers).

**Context:** README §Screens 2 (archive `3a`) + §Screens 3 (restore `3b`) + §Screens 4 (cost dialog). ONE component drives both kinds (palette + tiles + stage list switch on `kind`). On open: `getJob(id)` for the row + `attachToJob(id)` for the live snapshot (then `jobProgress` deltas). Reconnect re-attach is automatic (Task 1). The layered bar reads byte fields; the cost modal renders from a live `CostEstimate` push (captured per jobId); warnings load lazily via `getJobWarnings`.

Layers (percent of track), archive: scanned `scannedBytes/totalBytes`, middle `hashedBytes/totalBytes`, top `uploadedBytes/totalNewBytes`. Restore: scanned 100 (planned), middle `(chunksAvailable+chunksRehydrated)/(planned chunks)` — use `bytesRestored`-independent chunk ratio via `chunksAvailable+chunksRehydrated` over `chunksAvailable+chunksRehydrated+chunksPending`, top `bytesRestored/restoreTotalBytes`.

- [ ] **Step 1: Add the route (`app.routes.ts`)**

Insert before the `jobs` list route (so `jobs/:id` is matched as a sibling):

```typescript
  {
    path: 'jobs/:id',
    loadComponent: () => import('./features/jobs/job-detail.component').then(m => m.JobDetailComponent),
  },
```

- [ ] **Step 2: Create `job-detail.component.ts`**

Full component. Reproduce the `3a`/`3b` layout from `docs/history/superpowers/handoff-jobs-progress-ux/README.md` §Screens 2–4 and `Progress UX Options.dc.html`. The header, layered bar + legend, KPI tiles (4-up grid, `#fafafb`/`#f0f0f2`/radius 11px), stage list (bordered card, 9px dots, running dot pulses), footer (Cancel job + "There are N warnings"), warnings panel (amber card, monospace, `white-space:pre`, horizontal scroll — verbatim lines), and the cost modal (480px, two priority cards). Data wiring:

```typescript
import { ChangeDetectionStrategy, Component, computed, inject, input, signal, OnDestroy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api/api.service';
import { RealtimeService } from '../../core/api/realtime.service';
import { JobSnapshot, CostEstimateMsg, JobDetailDto, JobOutcome, isNonTerminal } from '../../core/api/api-models';
import { LayeredBarComponent } from '../../shared/layered-bar/layered-bar.component';
import { formatBytes } from '../../shared/format';
import { formatEta, formatDuration, formatThroughput, hydratedByLabel, statusMeta, phaseSentence } from '../../shared/job-format';
import { Subscription } from 'rxjs';

@Component({
  selector: 'arius-job-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, LayeredBarComponent],
  template: `…`,   // reproduce 3a/3b: header + ETA · layered bar + legend · KPI tiles · stage list · footer · warnings panel · cost modal
  styles: [`@keyframes ar-pulse { 50% { opacity:.45 } }`],
})
export class JobDetailComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  readonly id = input.required<string>();   // route param (withComponentInputBinding — verify enabled; see Step 3)

  protected readonly detail = signal<JobDetailDto | null>(null);
  protected readonly snap = signal<JobSnapshot | null>(null);
  protected readonly status = signal<string>('running');
  protected readonly cost = signal<CostEstimateMsg | null>(null);
  protected readonly warningsOpen = signal(false);
  protected readonly warnings = signal<string[]>([]);
  protected readonly priority = signal<'standard' | 'high'>('high');
  private subs: Subscription[] = [];
  private currentId = '';

  protected readonly kind = computed(() => this.detail()?.kind === 'restore' ? 'restore' : 'archive');
  protected readonly meta = computed(() => statusMeta(this.status()));
  protected readonly outcome = computed<JobOutcome | null>(() => { const o = this.detail()?.outcome; return o ? JSON.parse(o) as JobOutcome : null; });

  // layered-bar percentages
  protected readonly scannedPct = computed(() => { const s = this.snap(); return s && s.totalBytes ? s.scannedBytes * 100 / s.totalBytes : (this.kind() === 'restore' ? 100 : 0); });
  protected readonly middlePct  = computed(() => { const s = this.snap(); if (!s) return 0;
    return this.kind() === 'restore'
      ? (s.chunksAvailable + s.chunksRehydrated + s.chunksPending ? (s.chunksAvailable + s.chunksRehydrated) * 100 / (s.chunksAvailable + s.chunksRehydrated + s.chunksPending) : 0)
      : (s.totalBytes ? s.hashedBytes * 100 / s.totalBytes : 0); });
  protected readonly topPct     = computed(() => { const s = this.snap(); if (!s) return 0;
    return this.kind() === 'restore'
      ? (s.restoreTotalBytes ? s.bytesRestored * 100 / s.restoreTotalBytes : 0)
      : (s.totalNewBytes ? s.uploadedBytes * 100 / s.totalNewBytes : 0); });

  constructor() {
    // React to the route id: load the row + attach for live state.
    effectOnId(this);   // implement as an Angular effect() reading this.id(); pattern below
  }

  attach(id: string): void {
    if (id === this.currentId) return;
    this.teardown(); this.currentId = id;
    this.api.getJob(id).subscribe(d => { this.detail.set(d); this.status.set(d.status); if (d.snapshot) this.snap.set(d.snapshot); });
    void this.realtime.attachToJob(id).then(st => { if (st && this.currentId === id) { this.snap.set(st.snapshot); this.status.set(st.status); if (st.cost) this.cost.set(st.cost); } });
    this.subs.push(this.realtime.jobProgress(id).subscribe(s => this.snap.set(s)));
    this.subs.push(this.realtime.jobCost(id).subscribe(c => this.cost.set(c)));
    this.subs.push(this.realtime.jobDone(id).subscribe(d => { this.status.set(d.status); this.cost.set(null); this.api.getJob(id).subscribe(x => this.detail.set(x)); }));
  }

  protected phaseSentence = phaseSentence;
  protected formatBytes = formatBytes; protected formatEta = formatEta; protected formatDuration = formatDuration;
  protected formatThroughput = formatThroughput; protected hydratedByLabel = hydratedByLabel; protected isNonTerminal = isNonTerminal;

  protected toggleWarnings(): void {
    const open = !this.warningsOpen(); this.warningsOpen.set(open);
    if (open) this.api.getJobWarnings(this.currentId).subscribe(w => this.warnings.set(w.lines));
  }
  protected cancel(): void {
    if (this.status() === 'rehydrating' && !confirm('Rehydration is already paid — cancelling does not refund it. Cancel anyway?')) return;
    void this.realtime.cancelJob(this.currentId);
  }
  protected approve(): void { void this.realtime.approveRestore(this.currentId, this.priority()); this.cost.set(null); }
  protected decline(): void { void this.realtime.declineRestore(this.currentId); this.cost.set(null); }
  protected setAutoResume(on: boolean): void { void this.realtime.setAutoResume(this.currentId, on); }
  protected resumeNow(): void { void this.realtime.resumeRestore(this.currentId); }

  ngOnDestroy(): void { if (this.currentId) void this.realtime.detachFromJob(this.currentId); this.teardown(); }
  private teardown(): void { this.subs.forEach(s => s.unsubscribe()); this.subs = []; }
}
```

**Implementer note (wiring detail):** replace the `effectOnId(this)` placeholder with a real Angular `effect(() => this.attach(this.id()))` in the constructor (import `effect`). The template is a faithful reproduction of `3a`/`3b`; build it from the README's exact tokens/copy and the mockup markup — the data bindings are: header status chip `meta()`, ETA `formatEta(snap()?.etaSeconds)` + `formatThroughput`, `<arius-layered-bar [kind]="kind()" [scanned]="scannedPct()" [middle]="middlePct()" [top]="topPct()" />`, legend from byte fields, KPI tiles from `snap()`/`outcome()`, the restore rehydration wait card with the auto-resume toggle (`setAutoResume`) + "Restore now" (`resumeNow`) shown when `status()==='rehydrating'`, footer Cancel + "There are {{ snap()?.warningCount }} warnings" toggling the warnings panel, and the cost modal shown when `cost()` is non-null (two priority cards using `standardWaitHours`/`highWaitHours` for the "up to Nh" copy — **not** hardcoded). Every actionable element gets a `data-testid`: `job-detail`, `job-status`, `job-cancel`, `warnings-toggle`, `warnings-panel`, `cost-modal`, `prio-standard`, `prio-high`, `cost-approve`, `cost-decline`, `autoresume-toggle`, `restore-now`.

- [ ] **Step 3: Verify route input binding**

`app.config.ts` must use `provideRouter(routes, withComponentInputBinding())` for `readonly id = input.required<string>()` to bind the `:id` param. Check `src/app/app.config.ts`; if `withComponentInputBinding()` is absent, add it (it is already used by `repo-detail.component.ts`'s `repoId` input, so it is almost certainly present — confirm).

- [ ] **Step 4: Build gate + spec + commit**

Run: `npx ng build --configuration development` → clean.
Add `e2e/specs/job-detail.spec.ts` (skeleton — navigate to a finished job's `/jobs/:id`, assert `job-detail` + layered bar + status chip render; for a `@write` restore, assert the cost modal + approve flow). Update `cost-approval.spec.ts` so the cost modal is asserted on `/jobs/:id` (moved off the drawer).

```bash
git add src/Arius.Web/src/app/features/jobs/job-detail.component.ts src/Arius.Web/src/app/app.routes.ts src/Arius.Web/e2e/specs/job-detail.spec.ts src/Arius.Web/e2e/specs/cost-approval.spec.ts
git commit -m "feat(web): /jobs/:id detail page (archive + restore) with reattach, cost modal, warnings, cancel"
```

---

## Task 5: Jobs overview redesign

**Files:**
- Modify: `src/app/features/jobs/jobs.component.ts`

**Interfaces:**
- Consumes: Task 1 (`getJobs`, `jobProgress`), Task 2 (`LayeredBarComponent`, `phaseSentence`, `statusMeta`, formatters), `ApiService.getSchedules` (via listing repositories → schedules; or a count — see below).

**Context:** README §Screens 5 (mockup `4a`). Replaces the current table + live console. Three sections: **Needs your attention** (amber rows for `awaiting-cost`, and `rehydrating` with auto-resume off — but the client can't read auto-resume from the list, so treat `awaiting-cost` as needs-attention and `rehydrating` as Active per design §12's simplification for the list), **Active** (grid `260px 1fr 190px 110px`: identity · layered mini-bar (8px) + phase sentence · status chip + ETA · **Reattach ›**), **Scheduled & history** (running-bar column replaced by the one-line `outcome` summary; actions Edit ›/Report ›; history rows at .66 opacity). Heading chips: running = count(`running`); waiting = count(`awaiting-cost`)+count(`rehydrating`); scheduled = count(enabled schedules). Remove the `LiveConsoleComponent` import + the `consoleLines`/`realtime.log$` subscription.

- [ ] **Step 1: Rewrite `jobs.component.ts`**

Drive the list from `getJobs()` (poll or one-shot + live `jobProgress` overlay for the Active rows). Keep it OnPush + signals. Delete the `LiveConsoleComponent` import, the `consoleLines` signal, and the `realtime.log$` subscription in the constructor. Structure:

- `jobs = toSignal(this.api.getJobs())` (all jobs).
- `active = computed(() => jobs()?.filter(j => isNonTerminal(j.status)))`, split into `needsAttention` (`status==='awaiting-cost'`) and `running` (the rest of non-terminal).
- `history = computed(() => jobs()?.filter(j => !isNonTerminal(j.status)))`.
- Heading chips: `runningCount` = count(`running`), `waitingCount` = count(`awaiting-cost`)+count(`rehydrating`), `scheduledCount` = enabled-schedule count (fetch via `forkJoin` over repos' `getSchedules`, or expose a simpler count — if that is heavy, show scheduled = 0 for now and note it as a follow-up).
- For each Active row: `<arius-layered-bar height="8" [kind] [scanned][middle][top] />` fed by a per-row live `jobProgress(job.id)` snapshot (attach a lightweight subscription per visible active row, cleaned up on destroy) + `phaseSentence`. **Reattach ›** = `routerLink=['/jobs', job.id]`.
- History rows: parse `job.outcome` (JSON `JobOutcome`) → the one-liner (`3,412 files · 1.2 GB uploaded · 5.7 GB deduped · snapshot <timestamp> · 14 min` for archive; `811 files · 0.72 GB · 8 min` for restore). Actions `Report ›` = `routerLink=['/jobs', job.id]`; `Edit ›` opens schedules (existing).

Match the mockup `4a` grid, section headers, amber banner rows, and status chips exactly (README §Screens 5 + §Design Tokens). Preserve `data-testid="job-row"` and `data-testid="job-status"` (existing hooks); add `data-testid="jobs-needs-attention"`, `jobs-active`, `jobs-history`, `job-reattach`, `job-review-cost`.

- [ ] **Step 2: Build gate + spec + commit**

Run: `npx ng build --configuration development` → clean.
Update `e2e/specs/jobs.spec.ts` to assert the new sections + that the console is gone (`await expect(page.getByText('Live output')).toHaveCount(0)`), and that `Reattach ›` navigates to `/jobs/:id`.

```bash
git add src/Arius.Web/src/app/features/jobs/jobs.component.ts src/Arius.Web/e2e/specs/jobs.spec.ts
git commit -m "feat(web): redesigned Jobs overview (needs-attention / active / history), console removed"
```

---

## Task 6: Cutover — form-only drawer, remove legacy stream, delete the console

**Files:**
- Modify: `src/app/core/state/drawer.store.ts`, `src/app/features/drawer/archive-restore-drawer.component.ts`, `src/app/core/api/realtime.service.ts`, `src/app/core/api/api-models.ts`
- Delete: `src/app/shared/live-console/live-console.component.ts`

**Interfaces:**
- Removes: `RealtimeService.log$`, `progress$`/`cost$`/`done$` are **kept** (now consumed by the pill/detail via `jobProgress`/`jobCost`/`jobDone`) — only `log$` and the console go. `DrawerStore` loses `streamState`/`lines`/`progress`/`stats`/`cost`/`summary` and its `realtime.*$` subscriptions; keeps the form signals + `start()` (which now only starts + hands to the pill) + `close()`.

**Context:** This is the cutover — done last so removing the legacy path doesn't break the intermediate tasks. After it, the drawer is a pure form, the console is gone, and all live progress flows through the pill + detail page + overview.

- [ ] **Step 1: Strip `DrawerStore` to form-only**

Remove `streamState`, `lines`, `progress`, `stats`, `cost`, `summary`, `jobId` (keep `jobId` only if still handed to the pill — it can be a local in `start()`), the constructor's four `realtime.*$.subscribe(...)`, `resetStream()`, and `approve()`. Keep `type`/`repoId`/`accountId`/`version`/`collectedPaths`/`accountsRevision` + the form signals (`archiveTier`, `archiveOnDisk`, `fastHash`, `overwrite`, `restoreNoPointers`) + `openArchive`/`openRestore`/`openProperties`/`openAccount`/`close`. `start()` becomes:

```typescript
  async start(): Promise<void> {
    const kind = this.type();
    let id: string;
    if (kind === 'archive') {
      id = await this.realtime.startArchive(this.repoId(), { tier: this.archiveTier(), removeLocal: this.archiveOnDisk() === 'replace', writePointers: this.archiveOnDisk() !== 'keep', fastHash: this.fastHash() });
    } else {
      id = await this.realtime.startRestore(this.repoId(), { version: this.version(), targetPaths: this.collectedPaths(), overwrite: this.overwrite(), noPointers: this.restoreNoPointers() });
    }
    this.pill.show(id, kind === 'restore' ? 'restore' : 'archive');
    this.type.set(null);   // dismiss the drawer — the pill takes over
  }
```

`close()` becomes just `this.type.set(null);` (no cost-decline-on-close — the cost modal now lives on the detail page, not the drawer). If `StartArchive`/`StartRestore` throws the busy `HubException`, surface it (a toast/inline error) — a minimal `error` signal set in a try/catch is acceptable; note it as a `data-testid="start-error"`.

- [ ] **Step 2: Rewrite `archive-restore-drawer.component.ts` as form-only**

Remove the `LiveConsoleComponent` import, the entire stream view (the `@else` block), the cost modal, and the `stateLabel`/`statEntries`/`priority`/`approve`/`decline`/`onScrim`-stream-guard logic. The drawer is now: header + the idle form (archive form or restore form, exactly as today's `streamState()==='idle'` branch) + footer (`Close` / `Start archive|restore`). The footer's Start button calls `store.start()`. Surface the busy-repo error if present. Keep all form `data-testid`s (`drawer`, `drawer-title`, `tier-seg`, `seg-on-disk`, `toggle-fast-hash`, `drawer-start`).

- [ ] **Step 3: Remove `log$` from `realtime.service.ts` + delete the console**

- Delete the `readonly log$` field, the `this.connection.on('Log', …)` binding, and the `LogLine` import in `realtime.service.ts`. (Leave `startArchive`/`startRestore`/`approve`/streams intact.)
- Delete `src/app/shared/live-console/live-console.component.ts`.
- Remove the `LogLine` interface from `api-models.ts`.
- `grep -rn "live-console\|LiveConsole\|log\$\|LogLine" src/app` must return **zero** hits.

- [ ] **Step 4: Build gate + specs + commit**

Run: `npx ng build --configuration development` → clean (this is the true full-cutover build; all legacy references must be gone).
Update `archive.spec.ts` + `restore.spec.ts` + `restore-roundtrip.spec.ts`: after Start, the drawer closes and progress is observed on the **pill** / `/jobs/:id` (not the drawer stream/console). Move any console/stream assertions to the pill + detail page.

```bash
git add src/Arius.Web/src/app/core/state/drawer.store.ts src/Arius.Web/src/app/features/drawer/archive-restore-drawer.component.ts src/Arius.Web/src/app/core/api/realtime.service.ts src/Arius.Web/src/app/core/api/api-models.ts src/Arius.Web/e2e/specs/
git rm src/Arius.Web/src/app/shared/live-console/live-console.component.ts
git commit -m "feat(web): cutover — form-only drawer, remove live console + log stream"
```

---

## Self-review checklist (completed)

**Spec coverage (README §What to build):**
1. Kill the console → Task 6 (delete component + `log$` + all usages; zero-grep gate).
2. Drawer → pill flow → Task 3 (pill + hand-off) + Task 6 (drawer form-only, dismiss on Start).
3. Job detail page `/jobs/:id` → Task 4 (one component, archive + restore, reattach, cost modal, warnings, cancel, auto-resume/restore-now).
4. Jobs overview redesign → Task 5 (needs-attention / active + mini-bar + Reattach / history + outcome one-liner).
5. Api additions → already delivered by Plans 1–2; this plan only consumes them (Task 1 contracts).
- Layered bar (core idea) → Task 2 `LayeredBarComponent`, used at 14px (detail) + 8px (overview).
- Pill (1c) → Task 3. Reattach/snapshot-on-subscribe → Tasks 1 (attach + reconnect) + 3/4 (consumers).
- Cost dialog (3b lower) → Task 4 (on the detail page; two priority cards; wait windows from the estimate, not hardcoded).
- Rehydration wait card + auto-resume toggle + "Restore now" → Task 4.
- Status vocab / chips → Task 2 `statusMeta` + Tasks 4/5.

**Deliberate simplifications (flag at handoff):**
1. **Cost-on-reattach:** Plan 2 returns `JobAttachState.cost = null`, so a page reload of an `awaiting-cost` job shows the "Review cost" status but the estimate modal only renders from a live `CostEstimate` push during the session. Acceptable per the Plan-2 deferral; a future Api change could persist+replay the estimate.
2. **Overview needs-attention membership:** the list can't read the per-job `autoResume` flag (not on `JobDto`), so `rehydrating` rows sit under **Active** and only `awaiting-cost` sits under **Needs attention** (design §12 notes needs-attention should also include `rehydrating` when auto-resume is off — deferred until `JobDto` carries the flag).
3. **Scheduled chip count:** if fetching every repo's schedules for the heading count is too heavy, Task 5 may show scheduled=0 initially and wire the count as a follow-up.
4. **Pill glance-popover (mockup 2a)** omitted as optional polish; "View job ›" is the required navigation.

**Placeholder scan:** the only non-literal step is Task 4's template body (`template: '…'`) — deliberately delegated to the vendored mockup + README with an explicit implementer note listing every binding + `data-testid`; the data wiring (component class) is complete. The `effectOnId(this)` placeholder is called out with its real form (`effect(() => this.attach(this.id()))`).

**Type consistency:** `JobSnapshot`/`CostEstimateMsg`/`DoneMsg`/`JobAttachState`/`JobDetailDto`/`JobWarningsDto`/`JobOutcome` (Task 1) are consumed unchanged in Tasks 2–5; `LayeredBarComponent` inputs (`kind`/`height`/`scanned`/`middle`/`top`, Task 2) match every call site (Tasks 3/4/5); `JobPillStore` API (Task 3) matches the drawer + repo-detail callers; `RealtimeService` new methods (Task 1) match the pill/detail/overview callers; `statusMeta`/`phaseSentence`/`formatEta` signatures (Task 2) match all consumers.

**Build-green ordering:** Task 1 is additive (keeps `log$`/`progress$`/`cost$`/`done$` + zero-arg `getJobs()`), so the legacy drawer/overview keep compiling. Tasks 2–5 add the new surfaces. Task 6 removes the legacy path only after every consumer is on the new one — so each task ends at a green `ng build`.

**Testing reality:** the executable gate is `npx ng build` (typecheck/template). Playwright e2e specs are authored/updated per task and run in CI against a live backend. In a `node`-broken environment neither runs locally — the human/CI runs them before merge; each report must state whether `ng build` was actually executed or read-through only.
