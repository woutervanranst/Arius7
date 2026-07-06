# Jobs progress — Plan 3b: client rendering + Vitest + #1 lock-in test

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring back a web unit-test runner (**Vitest**, wired to Codecov), render the reattach state Plan 3a now serves (auto-resume toggle #14, rehydration-window "≈ hydrated by" #13; cost modal #2 already flows via `attachToJob`), make the `/jobs` list update live when a job finishes (#7), move the pill to center-bottom and drop the `" photos"` leftover, and lock in the by-design single-active-job behavior (#1) with a hermetic Api-integration test.

**Architecture:** Standalone Vitest (Angular 21 uses `@angular/build`; Karma/Jasmine were removed) tests the pure functions in `job-format.ts`; the existing `job-format.spec.ts` runs under it unchanged (`globals:true`). Client reattach reads a new `resume` field mirrored from the server `ResumeInfo`; the rehydration-window resolution is extracted into a pure, Vitest-tested helper. `/jobs` becomes re-fetchable and reloads on `jobDone`. The #1 scenario is a hermetic `Arius.Api.Integration.Tests` case on the Plan-1 harness.

**Tech Stack:** Angular 21 (`@angular/build:application`/`:dev-server`), Vitest + jsdom, Codecov, TUnit + the scripted-fake-Core harness.

## Global Constraints

- **No `Arius.Core` changes.**
- **Do not reintroduce Karma/Jasmine** (deprecated). Vitest is the runner. The existing `src/Arius.Web/src/app/shared/job-format.spec.ts` uses global `describe/it/expect` — configure Vitest `globals:true` so it runs unmodified.
- Web build gate: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors. Web test gate: `npm test` (Vitest) green.
- Codecov: **Vitest coverage** (v8 → lcov) uploaded with a `web` flag; **Playwright stays report-only** (behavioral gate, not a coverage source) — per the CI decision.
- The reattach cost modal (#2) already renders from `attachToJob().then(st => … st.cost)` now that Plan 3a populates `JobAttachState.cost`; this plan makes the **resume** facts (#13/#14) flow the same way and adds coverage.

## File structure

**New (Vitest):** `src/Arius.Web/vitest.config.ts`; `src/Arius.Web/tsconfig.spec.json`; `package.json` scripts+devDeps; `.github/workflows/ci.yml` (a `web-unit` job or step).
**Client:** `src/Arius.Web/src/app/core/api/api-models.ts` (`ResumeInfo` + `resume`/`cost` fields); `src/Arius.Web/src/app/shared/job-format.ts` (`resolveRehydrationWindowHours` helper) + `job-format.spec.ts` (tests); `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` (seed auto-resume/window on attach); `src/Arius.Web/src/app/features/jobs/jobs.component.ts` (re-fetchable + `jobDone`); `src/Arius.Web/src/app/features/pill/job-pill.component.ts` (center-bottom + drop `" photos"`); `src/Arius.Web/src/app/core/api/realtime.service.ts` (remove dead `approve()`).
**Test (server):** `src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs` (new, #1).

---

## Task 1: Vitest runner + Codecov wiring

**Files:**
- Create: `src/Arius.Web/vitest.config.ts`, `src/Arius.Web/tsconfig.spec.json`
- Modify: `src/Arius.Web/package.json`, `.github/workflows/ci.yml`

**Interfaces:** Produces an `npm test` (Vitest) that runs `**/*.spec.ts` under `src/app`, and a CI job uploading web coverage to Codecov with the `web` flag.

- [ ] **Step 1: Add Vitest devDeps**

Run (from `src/Arius.Web`): `npm install -D vitest@^3 jsdom @vitest/coverage-v8`
(These are dev-only; they don't touch the app bundle. `@angular/build` + Vite are already present transitively.)

- [ ] **Step 2: Create `vitest.config.ts`**

`src/Arius.Web/vitest.config.ts`:

```ts
import { defineConfig } from 'vitest/config';

// Standalone Vitest for pure TS logic (job-format et al.). The existing specs use global describe/it,
// so globals:true. Angular component tests would need @analogjs/vitest-angular — out of scope here.
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/app/**/*.spec.ts'],
    // e2e specs are Playwright, not Vitest:
    exclude: ['e2e/**', 'node_modules/**'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcovonly'],
      reportsDirectory: './coverage',
      include: ['src/app/**/*.ts'],
      exclude: ['src/app/**/*.spec.ts', 'src/app/**/*.component.ts'],  // components need the Angular harness; cover pure logic
    },
  },
});
```

- [ ] **Step 3: Create `tsconfig.spec.json`** (so editors/type-checkers see the spec files)

`src/Arius.Web/tsconfig.spec.json`:

```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": {
    "outDir": "./out-tsc/spec",
    "types": ["vitest/globals", "node"]
  },
  "include": ["src/app/**/*.spec.ts", "src/**/*.d.ts"]
}
```

- [ ] **Step 4: Add scripts to `package.json`**

Add to the `scripts` block:

```json
    "test": "vitest run",
    "test:watch": "vitest",
    "test:coverage": "vitest run --coverage",
```

- [ ] **Step 5: Run the existing spec**

Run: `cd src/Arius.Web && npm test`
Expected: `job-format.spec.ts` runs GREEN (the 3 `describe` blocks from Plan 2 Task 5 — archiveBarLayers/restoreBarLayers/phaseSentence). If the global `describe/it` aren't found, confirm `globals:true` and the `vitest/globals` type. Confirm a non-zero test count.

- [ ] **Step 6: Wire Codecov in CI**

In `.github/workflows/ci.yml`, add a `web-unit` job (sibling to `e2e`) that runs Vitest with coverage and uploads to Codecov with the `web` flag:

```yaml
  web-unit:
    name: 🧪 Web unit (Vitest)
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/Arius.Web
    steps:
      - name: 🛎️ Checkout
        uses: actions/checkout@v7
      - name: ⚙️ Setup Node
        uses: actions/setup-node@v6
        with:
          node-version: "22"
          cache: npm
          cache-dependency-path: src/Arius.Web/package-lock.json
      - name: 📦 Install dependencies
        run: npm ci
      - name: 🧪 Vitest with coverage
        run: npm run test:coverage
      - name: 📈 Upload coverage to Codecov
        if: ${{ always() }}
        uses: codecov/codecov-action@v7
        with:
          files: src/Arius.Web/coverage/lcov.info
          flags: web
          name: coverage-web
          token: ${{ secrets.CODECOV_TOKEN }}
```

(`@vitest/coverage-v8`'s `lcovonly` reporter writes `coverage/lcov.info`. Confirm the path after a local `npm run test:coverage`.)

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Web/vitest.config.ts src/Arius.Web/tsconfig.spec.json src/Arius.Web/package.json src/Arius.Web/package-lock.json .github/workflows/ci.yml
git commit -m "test(web): add Vitest unit runner + Codecov web coverage (runs the dormant job-format spec)"
```

---

## Task 2: Client reattach — auto-resume (#14) + rehydration window (#13)

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/api-models.ts` (`ResumeInfo`, `resume`/`cost` fields)
- Modify: `src/Arius.Web/src/app/shared/job-format.ts` (`resolveRehydrationWindowHours`) + `job-format.spec.ts`
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` (seed from `st.resume`)

**Interfaces:**
- Produces: `ResumeInfo { autoResume: boolean; rehydrationStartedAt: string; rehydrationWindowHours: number }`; `JobAttachState.resume: ResumeInfo | null`; `JobDetailDto.cost: CostEstimateMsg | null` + `JobDetailDto.resume: ResumeInfo | null`; `resolveRehydrationWindowHours(cost, resume, priority)`.

**Background:** Plan 3a now returns `cost` + `resume` on `AttachToJob`/`GET /jobs/{id}`. The client's `attach()` already seeds `cost` (so #2's modal renders on reattach), but never reads `resume` — so the auto-resume toggle stays hardcoded `signal(true)` (#14) and `hydratedBy` is empty for a `rehydrating` job because `rehydrateWindowHours()` only reads `cost()` which is null past approval (#13).

- [ ] **Step 1: Mirror the server DTOs** — `api-models.ts`

```typescript
export interface ResumeInfo {
  autoResume: boolean;
  rehydrationStartedAt: string;
  rehydrationWindowHours: number;
}
```
Add `resume: ResumeInfo | null;` to `JobAttachState` (after `warningCount`). Add `cost: CostEstimateMsg | null;` and `resume: ResumeInfo | null;` to `JobDetailDto` (after `warningCount`).

- [ ] **Step 2: Write the failing test** for the window helper (append to `job-format.spec.ts`)

```typescript
import { resolveRehydrationWindowHours } from './job-format';
import { CostEstimateMsg, ResumeInfo } from '../core/api/api-models';

describe('resolveRehydrationWindowHours', () => {
  const cost = (p: Partial<CostEstimateMsg>): CostEstimateMsg => ({ jobId: 'j', chunksAvailable: 0, chunksNeedingRehydration: 0, bytesNeedingRehydration: 0, downloadBytes: 0, totalStandard: 0, totalHigh: 0, standardWaitHours: 15, highWaitHours: 1, ...p });
  const resume = (p: Partial<ResumeInfo>): ResumeInfo => ({ autoResume: true, rehydrationStartedAt: '2026-01-01T00:00:00Z', rehydrationWindowHours: 15, ...p });

  it('prefers the live cost estimate when present (priority-aware)', () => {
    expect(resolveRehydrationWindowHours(cost({ highWaitHours: 1 }), null, 'high')).toBe(1);
    expect(resolveRehydrationWindowHours(cost({ standardWaitHours: 15 }), null, 'standard')).toBe(15);
  });
  it('falls back to the persisted resume window when cost is null (rehydrating past approval)', () => {
    expect(resolveRehydrationWindowHours(null, resume({ rehydrationWindowHours: 15 }), 'standard')).toBe(15);
  });
  it('returns null when neither is available', () => {
    expect(resolveRehydrationWindowHours(null, null, 'high')).toBeNull();
  });
});
```

- [ ] **Step 3: Run RED** — `cd src/Arius.Web && npm test` → the new `resolveRehydrationWindowHours` block FAILS (function undefined).

- [ ] **Step 4: Add the helper** — `job-format.ts`

```typescript
import { CostEstimateMsg, JobSnapshot, ResumeInfo } from '../core/api/api-models';

/** The rehydration SLA window (hours) for the "≈ hydrated by" ETA: the live cost estimate when present
 *  (priority-aware), else the persisted resume window (so a rehydrating job past cost-approval still shows
 *  an ETA), else null. */
export function resolveRehydrationWindowHours(
  cost: CostEstimateMsg | null, resume: ResumeInfo | null, priority: 'standard' | 'high'): number | null {
  if (cost) return priority === 'high' ? cost.highWaitHours : cost.standardWaitHours;
  if (resume) return resume.rehydrationWindowHours;
  return null;
}
```
(Merge the `ResumeInfo` import into the existing `api-models` import line.)

- [ ] **Step 5: Use it + seed resume in the component** — `job-detail.component.ts`

Add a `resume` signal near the others (`:298`):
```typescript
  protected readonly resume = signal<ResumeInfo | null>(null);
```
Import `ResumeInfo` from `api-models`.

Replace `rehydrateWindowHours` (`:335-338`):
```typescript
  protected readonly rehydrateWindowHours = computed<number | null>(() =>
    resolveRehydrationWindowHours(this.cost(), this.resume(), this.priority()));
```
Import `resolveRehydrationWindowHours` from `../../shared/job-format`.

In `attach()` (`:411-422`), seed `resume` (and `autoResume`) from both the `getJob` and `attachToJob` results:
```typescript
    this.api.getJob(id).subscribe(d => {
      if (this.currentId !== id) return;
      this.detail.set(d); this.status.set(d.status); if (d.snapshot) this.snap.set(d.snapshot);
      if (d.cost) this.cost.set(d.cost);
      if (d.resume) { this.resume.set(d.resume); this.autoResume.set(d.resume.autoResume); }
    });
    void this.realtime.attachToJob(id).then(st => { if (st && this.currentId === id) {
      this.snap.set(st.snapshot); this.status.set(st.status);
      if (st.cost) this.cost.set(st.cost);
      if (st.resume) { this.resume.set(st.resume); this.autoResume.set(st.resume.autoResume); }
    } });
```
(Leave the `jobProgress`/`jobCost`/`jobDone` subscriptions as-is.)

- [ ] **Step 6: Run GREEN + build**

Run: `cd src/Arius.Web && npm test` → all specs pass (incl. the new window block).
Run: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Web/src/app/core/api/api-models.ts src/Arius.Web/src/app/shared/job-format.ts src/Arius.Web/src/app/shared/job-format.spec.ts src/Arius.Web/src/app/features/jobs/job-detail.component.ts
git commit -m "fix(web): seed auto-resume (#14) + rehydration-window ETA (#13) from reattach state"
```

---

## Task 3: `/jobs` list live-update on completion (#7)

**Files:**
- Modify: `src/Arius.Web/src/app/features/jobs/jobs.component.ts`

**Background:** `jobs = toSignal(this.api.getJobs())` is a one-shot; the effect subscribes only to `jobProgress`. So a finishing job never leaves the "Active" section and the count chips stay wrong until a manual reload. Fix: make the jobs list re-fetchable and reload it when any active row emits `jobDone`.

- [ ] **Step 1: Make `jobs` re-fetchable** — replace `toSignal(this.api.getJobs())` (`:165`):

```typescript
  private readonly jobsData = signal<JobDto[] | undefined>(undefined);
  protected readonly jobs = this.jobsData.asReadonly();
  private reload(): void { this.api.getJobs().subscribe(list => this.jobsData.set(list)); }
```
Call `this.reload();` in the constructor (before the effect).

- [ ] **Step 2: Subscribe `jobDone` per active row + reload** — in the constructor effect (`:213-226`), add a `jobDone` subscription alongside the existing `jobProgress` one:

```typescript
    effect(() => {
      for (const job of this.running()) {
        if (this.jobSubs.has(job.id)) continue;
        const sub = this.realtime.jobProgress(job.id).subscribe(snap => this.snapshots.update(m => ({ ...m, [job.id]: snap })));
        const doneSub = this.realtime.jobDone(job.id).subscribe(() => this.reload());   // finished → re-fetch so it leaves Active + chips update
        this.jobSubs.set(job.id, sub);
        this.doneSubs.set(job.id, doneSub);
        void this.realtime.attachToJob(job.id)
          .then(state => { if (state) this.snapshots.update(m => ({ ...m, [job.id]: state.snapshot })); })
          .catch(() => {});
      }
    });
```
Add `private readonly doneSubs = new Map<string, Subscription>();` next to `jobSubs`, and tear them down in `ngOnDestroy` alongside `jobSubs` (unsubscribe all + clear).

- [ ] **Step 3: Build**

Run: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors.
(Behavior — job finishes → `jobDone` fires → `reload()` re-fetches `/jobs` → the row's status is now terminal → `history()` includes it, `running()`/chips drop it. Full rendering is verified by Plan 3c's e2e; here it's build + logic review.)

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Web/src/app/features/jobs/jobs.component.ts
git commit -m "fix(web): /jobs list reloads on jobDone so finished rows leave Active (#7)"
```

---

## Task 4: Pill center-bottom + drop `" photos"` + remove dead `approve()`

**Files:**
- Modify: `src/Arius.Web/src/app/features/pill/job-pill.component.ts`
- Modify: `src/Arius.Web/src/app/core/api/realtime.service.ts`

- [ ] **Step 1: Center-bottom the pill** — `job-pill.component.ts:14`, replace `right:18px;bottom:16px` with center-bottom:

```
      <div data-testid="job-pill" style="position:fixed;left:50%;transform:translateX(-50%);bottom:16px;z-index:50;display:flex;align-items:center;gap:11px;
```
(Keep the rest of the style string.)

- [ ] **Step 2: Drop the `" photos"` leftover** — `job-pill.component.ts:46`:

```typescript
    return phaseSentence(s, kind).split(' —')[0];   // "Uploading" / "Restoring" style verb
```
(Remove the `+ (kind === 'archive' ? ' photos' : '')`. `kind` is still used above; if it becomes unused, drop the local.)

- [ ] **Step 3: Remove the dead `approve()` shim** — `realtime.service.ts` (`approve(jobId, priority)` → `invoke('Approve', …)`): the server has no `Approve` method (it's `ApproveRestore`/`DeclineRestore`), and grep shows no caller. Delete the `approve(...)` method and its doc comment.

Verify no caller: `grep -rn "\.approve(" src/Arius.Web/src/app` → zero hits (the modal's approve/decline use `approveRestore`/`declineRestore` on the component). If any hit exists, STOP and reconcile instead of deleting.

- [ ] **Step 4: Build**

Run: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Web/src/app/features/pill/job-pill.component.ts src/Arius.Web/src/app/core/api/realtime.service.ts
git commit -m "fix(web): center-bottom pill, drop leftover \" photos\" verb, remove dead approve() shim"
```

---

## Task 5: #1 by-design single-active-job — hermetic lock-in test

**Files:**
- Create: `src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs`

**Background:** #1 is *not* a bug — a repository holds at most one non-terminal job; a restore parked at `awaiting-cost` correctly blocks new archives/restores until cancelled. The owner asked for a regression test. This is fully exercised hermetically on the Plan-1 harness (no browser needed): park a restore at awaiting-cost, assert a second start is rejected, cancel it, then a start succeeds.

**Interfaces:** Consumes `AriusApiFactory`, `ScenarioRegistry`, `RestoreScenario`, `JobRunner`, `AppDatabase.HasActiveJob`, `RestoreApprovalRegistry`.

- [ ] **Step 1: Write the test**

`src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs`:

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class SingleActiveJobScenarioTests
{
    [Test]
    public async Task Restore_parked_at_awaiting_cost_blocks_new_jobs_until_cancelled()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 1, TotalOriginalSize: 100),
                new ChunkResolutionCompleteEvent(TotalChunks: 2, LargeCount: 1, TarCount: 0, TotalChunkBytes: 100),
                new RehydrationStatusEvent(Available: 0, Rehydrated: 0, NeedsRehydration: 2, Pending: 0),
            ],
            CostPrompt: new RestoreCostEstimate
            {
                ChunksAvailable = 0, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 2, ChunksPendingRehydration = 0,
                BytesNeedingRehydration = 100, BytesPendingRehydration = 0, DownloadBytes = 100,
                TotalStandard = 0.5, TotalHigh = 2.0, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var jobId = Guid.NewGuid().ToString();

        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await WaitUntil(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        // By design: the repo is busy while the restore is parked → HasActiveJob true, a new start is rejected.
        await Assert.That(db.HasActiveJob(repoId)).IsTrue();

        // Cancel the parked restore (decline) → frees the repo.
        approvals.Resolve(jobId, null);
        await WaitUntil(() => !db.HasActiveJob(repoId), TimeSpan.FromSeconds(10));
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");

        // Now a new job for the repo is accepted (guard clear).
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
```

> This asserts the *guard* (`HasActiveJob`) directly — the same check `JobsHub.StartArchive`/`StartRestore` and `SchedulerService` use to reject/skip. If you want the hub-level rejection too, drive `StartArchive` via a `HubConnection` while parked and assert the `HubException "A job is already running…"` — optional; the guard assertion is the core regression. Confirm `RestoreApprovalRegistry.Resolve(jobId, null)` routes the parked run to `cancelled` (the decline branch); if the run instead parks via timeout, adjust to drive `CancelJob`.

- [ ] **Step 2: Run + full suite**

Run: `dotnet test src/Arius.Api.Integration.Tests --treenode-filter "/*/*/SingleActiveJobScenarioTests/*"` → PASS (non-zero).
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → all green.

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs
git commit -m "test(api): lock in single-active-job — parked restore blocks new jobs until cancelled (#1)"
```

---

## Self-review

**Spec coverage:** Vitest+Codecov → Task 1 (the dormant `job-format.spec` now runs; web coverage flag added; Playwright stays report-only). #13/#14 client → Task 2 (`resume` mirror + `resolveRehydrationWindowHours` helper (Vitest-tested) + `attach()` seeds auto-resume/window). #2 client → already flows via `attach()`'s `st.cost` (Plan 3a populated it); Task 2 also seeds it from the `getJob` path for robustness. #7 → Task 3 (re-fetchable jobs + `jobDone` reload). Pill center-bottom + `" photos"` + dead `approve()` → Task 4. #1 → Task 5 (hermetic Api-integration lock-in).

**Placeholder scan:** no TBD/TODO. The `>` notes are verification/adaptation points (confirm the lcov path; confirm `Resolve(null)` routes to cancelled; grep for `.approve(` callers before deleting). Each names the exact command.

**Type consistency:** `ResumeInfo { autoResume, rehydrationStartedAt, rehydrationWindowHours }` matches the server `ResumeInfo(bool AutoResume, DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours)` (camelCase JSON). `resolveRehydrationWindowHours(cost, resume, priority)` signature is used identically in the helper, its test, and `job-detail`'s computed. `JobAttachState.resume`/`JobDetailDto.cost`+`resume` mirror Plan 3a's server DTOs.

**Deferred to Plan 3c (browser-hermetic e2e) or accepted:**
- **Browser e2e for #1/#2/#7** against a scripted-fake Api needs a `Testing`-gated control endpoint + extracting the harness (`src/Arius.Api.Integration.Tests/Harness/*`) into a lib the Api references, so Playwright can boot it out-of-process. That's a meaningful architectural piece — **Plan 3c**. Until then: #1/#2/#7 server+logic are covered by Api-integration (Tasks 5 + Plan 3a) + Vitest (Task 2), and the existing real-Azure Playwright specs remain the full-stack behavioral gate.
- **Component-level** rendering of the cost modal / toggle / hydratedBy is verified by `ng build` + Plan 3c e2e (standalone Vitest here covers only pure functions; Angular component tests would need `@analogjs/vitest-angular`).
- Carried cleanups (fold into a task where touched): `ToResumeInfo` duplicated across `JobsHub`/`JobEndpoints` (server); prune the now-no-op `sink.Log("meta"/"info")` calls; `ScriptedRestoreHandler` distinct declined-result shape (needed only when Plan 3c scripts a decline).
