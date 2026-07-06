# Jobs progress — Plan 3a: server lifecycle guards + reattach-state seeding

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the server-side job-lifecycle defects (review findings #3, #4, #10, #12, #15) and make parked/rehydrating restore state reconstructable on reattach (#2, #13, #14 — *server* side: persist the cost estimate + surface cost/auto-resume/rehydration-window via `AttachToJob` and `GET /jobs/{id}`). The matching *client* rendering is Plan 3b.

**Architecture:** Small, targeted edits to `RestoreApprovalRegistry` (atomic resolve-or-timeout), `AppDatabase.CompleteJob` (terminal-state guard) + a shared job-status constant, `JobRunner.ResumeRestoreAsync` (real start time), the two warning-count read sites, `JobSink.Log` (drop the dead SignalR send), and the attach/detail DTO path (persist + surface the cost estimate and resume info). All regression-tested with TUnit + the Plan-1 scripted-fake-Core harness.

**Tech Stack:** .NET 10, Martin Othamar `Mediator`, SQLite (`Microsoft.Data.Sqlite`), TUnit + Shouldly, the `Arius.Api.Integration.Tests` harness (`WebApplicationFactory` + scripted Core).

## Global Constraints

- **No `Arius.Core` changes.** All edits are in `Arius.Api` + its test projects.
- **Terminal statuses:** `completed | failed | cancelled | interrupted`. **Non-terminal:** `running | awaiting-cost | rehydrating`. (Today these are repeated string literals in ≥5 places; Task 2 introduces one shared constant.)
- Reattach must work for a **parked (`awaiting-cost`) or `rehydrating`** restore reached by a *fresh* connection (the live sink is gone) — reconstructed from `state_json`.
- `dotnet test --treenode-filter` (plain `--filter` silently runs 0 tests under TUnit/MTP).
- TUnit style: `[Test] public async Task` + `await Assert.That(x).IsEqualTo(y)`.

---

## File structure

- Modify `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs` — `RegisterAsync` timeout branch (#3).
- Create `src/Arius.Api/AppData/JobStatuses.cs` — shared terminal/non-terminal status lists (new).
- Modify `src/Arius.Api/AppData/AppDatabase.cs` — `CompleteJob` terminal guard; use the shared lists in `CompleteJob`/`HasActiveJob`/index DDL (#4).
- Modify `src/Arius.Api/Hubs/JobsHub.cs` — `CancelJob` (relies on the guard), `AttachToJob` (surface cost + resume + true warning count) (#4, #2, #12, #13, #14).
- Modify `src/Arius.Api/Jobs/JobRunner.cs` — `ResumeRestoreAsync` outcome start time (#10); persist the cost estimate in the awaiting-cost park branch (#2).
- Modify `src/Arius.Api/Jobs/JobSink.cs` — `Log` (drop dead send, #15); `BuildPersistedState` gains a cost param.
- Modify `src/Arius.Api/Jobs/PersistedJobState.cs` — add `Cost` (#2).
- Modify `src/Arius.Api/Contracts/JobDetailDtos.cs` — `ResumeInfo` record; `JobAttachState`/`JobDetailDto` gain `Cost`/`Resume` (#2/#13/#14).
- Modify `src/Arius.Api/Endpoints/JobEndpoints.cs` — `GET /jobs/{id}` warning count (#12) + surface cost/resume.
- Tests: extend `src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs`, `.../AppData/JobLifecycleDbTests.cs`; new `src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs` + `ReattachScenarioTests.cs`.

---

## Task 1: Approval-registry atomic resolve-or-timeout (fix #3)

**Files:**
- Modify: `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs:28-30`
- Test: `src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs`

**Interfaces:** `RegisterAsync`/`Resolve`/`HasPending` signatures unchanged; only the internal timeout resolution changes.

**Background:** `RegisterAsync` does `await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct))` and, if the delay won, returns `TimedOut` **without checking whether `tcs` was already completed by a concurrent `Resolve`** — so an approval that lands microseconds before the deadline is silently discarded. Fix: in the timeout branch, atomically claim the outcome via `tcs.TrySetResult`; if the claim fails, a real answer already landed — read and honor it.

- [ ] **Step 1: Write the failing/guarding tests** (append to `RestoreApprovalRegistryTests.cs`)

```csharp
    [Test]
    public async Task Approve_before_timeout_is_honored()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("j", TimeSpan.FromSeconds(5), CancellationToken.None);
        while (!reg.HasPending("j")) await Task.Yield();
        reg.Resolve("j", RehydratePriority.High);
        var r = await wait;
        await Assert.That(r.Approved).IsTrue();
        await Assert.That(r.Priority).IsEqualTo(RehydratePriority.High);
        await Assert.That(r.TimedOut).IsFalse();
    }

    [Test]
    public async Task Timeout_with_no_answer_reports_timed_out()
    {
        var reg = new RestoreApprovalRegistry();
        var r = await reg.RegisterAsync("j", TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Assert.That(r.TimedOut).IsTrue();
        await Assert.That(r.Approved).IsFalse();
    }

    [Test]
    public async Task Approval_racing_the_deadline_is_never_silently_dropped()
    {
        // Stress the WhenAny/Resolve interleaving: a real approval must yield Approved, or a genuine timeout
        // must yield TimedOut — never a non-approved, non-timed-out result (which would be a lost approval).
        var sawApproved = false;
        for (var i = 0; i < 300; i++)
        {
            var reg = new RestoreApprovalRegistry();
            var wait = reg.RegisterAsync($"j{i}", TimeSpan.FromMilliseconds(1), CancellationToken.None);
            var resolve = Task.Run(() => reg.Resolve($"j{i}", RehydratePriority.High));
            await Task.WhenAll(wait, resolve);
            var r = await wait;
            // Invariant: if not timed out, it must be an honored approval (never a dropped one).
            if (!r.TimedOut) { await Assert.That(r.Approved).IsTrue(); await Assert.That(r.Priority).IsEqualTo(RehydratePriority.High); sawApproved = true; }
        }
        await Assert.That(sawApproved).IsTrue();   // the honor-path actually fires under contention
    }
```

- [ ] **Step 2: Run RED**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RestoreApprovalRegistryTests/*"`
Expected: the race test is flaky/failing under the old code (a dropped approval surfaces as `!TimedOut && !Approved`, or `TimedOut` while an approval was set). The first two may already pass.

- [ ] **Step 3: Implement the fix** — replace `RestoreApprovalRegistry.cs:28-30`:

```csharp
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                // Timeout fired — but a concurrent Resolve may have completed the TCS between WhenAny returning
                // and now. Atomically claim the timeout; if the claim FAILS, a real answer landed first, so honor
                // it — otherwise a genuine approval that raced the deadline would be silently discarded (#3).
                if (tcs.TrySetResult(null))
                    return new ApprovalResult(Approved: false, Priority: null, TimedOut: true);
                var raced = await tcs.Task.ConfigureAwait(false);
                return new ApprovalResult(Approved: raced is not null, Priority: raced, TimedOut: false);
            }
```

- [ ] **Step 4: Run GREEN + full suite**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RestoreApprovalRegistryTests/*"` → all pass (run twice to confirm the race test is stable).
Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj` → all green.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/Jobs/RestoreApprovalRegistry.cs src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs
git commit -m "fix(api): approval registry honors an approval that races the timeout (no silent drop)"
```

---

## Task 2: `CompleteJob` terminal-state guard + shared status constant (fix #4)

**Files:**
- Create: `src/Arius.Api/AppData/JobStatuses.cs`
- Modify: `src/Arius.Api/AppData/AppDatabase.cs` (`CompleteJob:389`, `HasActiveJob:484`, index DDL `:116-120`)
- Modify: `src/Arius.Api/Endpoints/JobEndpoints.cs:16` (use the shared list)
- Test: `src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs`; `src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs` (new)

**Interfaces:**
- Produces: `Arius.Api.AppData.JobStatuses` with `string[] Terminal`, `string[] NonTerminal`, `const string TerminalSqlList`, `const string NonTerminalSqlList`. `CompleteJob` becomes a guarded UPDATE (terminal rows are not overwritten).

**Background:** `CompleteJob` is `UPDATE jobs SET status=…,pct=…,detail=…,finished_at=… WHERE id=$id` — unconditional. `CancelJob`'s fall-through branch (`JobsHub.cs:134`) calls it, so a Cancel racing the poller's completion overwrites a `completed` row with `cancelled` (pct→0). Fix: guard `CompleteJob` so it never overwrites a terminal row.

- [ ] **Step 1: Create the shared status lists**

`src/Arius.Api/AppData/JobStatuses.cs`:

```csharp
namespace Arius.Api.AppData;

/// <summary>The single source of truth for job status sets. Terminal = a finished row that must never be
/// re-transitioned; non-terminal = an active row the single-active-job guard counts. Kept here so the SQL
/// guards, the unique index, and the endpoint filters can't drift apart.</summary>
public static class JobStatuses
{
    public static readonly string[] Terminal    = ["completed", "failed", "cancelled", "interrupted"];
    public static readonly string[] NonTerminal = ["running", "awaiting-cost", "rehydrating"];

    // Compile-time constants for inlining into SQL WHERE clauses (no user input — safe from injection).
    public const string TerminalSqlList    = "'completed','failed','cancelled','interrupted'";
    public const string NonTerminalSqlList = "'running','awaiting-cost','rehydrating'";
}
```

- [ ] **Step 2: Write the failing tests** (append to `JobLifecycleDbTests.cs`)

```csharp
    [Test]
    public async Task CompleteJob_does_not_overwrite_an_already_terminal_row()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var db = new AppDatabase(dbPath);
        db.InsertJob("j", repositoryId: 1, kind: "restore", trigger: "one-off", status: "running");
        db.CompleteJob("j", "completed", 100, "Restore complete.");   // legitimate terminal transition

        db.CompleteJob("j", "cancelled", 0, "Cancelled.");            // racing cancel — must be a no-op now

        var job = db.GetJob("j")!;
        await Assert.That(job.Status).IsEqualTo("completed");         // NOT clobbered to cancelled
        await Assert.That(job.Pct).IsEqualTo(100d);                  // pct not reset to 0
    }

    [Test]
    public async Task CompleteJob_still_transitions_a_non_terminal_row()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var db = new AppDatabase(dbPath);
        db.InsertJob("j", repositoryId: 1, kind: "archive", trigger: "one-off", status: "running");
        db.CompleteJob("j", "completed", 100, "done");
        await Assert.That(db.GetJob("j")!.Status).IsEqualTo("completed");
    }
```

- [ ] **Step 3: Run RED**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/JobLifecycleDbTests/*"`
Expected: `CompleteJob_does_not_overwrite…` FAILS — the row is clobbered to `cancelled`/0.

- [ ] **Step 4: Guard `CompleteJob`** — replace the `CommandText` at `AppDatabase.cs:389`:

```csharp
        command.CommandText = "UPDATE jobs SET status = $status, pct = $pct, detail = $detail, finished_at = $finishedAt WHERE id = $id AND status NOT IN (" + JobStatuses.TerminalSqlList + ");";
```

- [ ] **Step 5: Use the shared lists in the sibling sites** (behavior-preserving DRY)

- `HasActiveJob` (`AppDatabase.cs:484`):
```csharp
        command.CommandText = "SELECT 1 FROM jobs WHERE repo_id = $r AND status IN (" + JobStatuses.NonTerminalSqlList + ") LIMIT 1;";
```
- The unique-index DDL (`AppDatabase.cs:116-120`):
```csharp
        index.CommandText = $"""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_one_active_per_repo
                ON jobs(repo_id)
                WHERE status IN ({JobStatuses.NonTerminalSqlList});
            """;
```
- `JobEndpoints.cs:16`:
```csharp
            var nonTerminal = new HashSet<string>(JobStatuses.NonTerminal);
```
(Leave `ReconcileRunningJobs` as-is unless it uses the exact same set — if it reconciles only `running`/`awaiting-cost`, that's a deliberate subset; do not fold it in.)

- [ ] **Step 6: Integration test — cancel racing completion** (`src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs`, new)

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class LifecycleScenarioTests
{
    [Test]
    public async Task Cancel_after_completion_does_not_clobber_the_completed_row()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.CompleteJob("j", "completed", 100, "Restore complete.");   // poller completed it

        // Cancel arrives after completion: JobsHub.CancelJob's fall-through calls the guarded CompleteJob.
        var hub = factory.Services.GetRequiredService<Arius.Api.Hubs.JobsHub>();  // if not resolvable, drive via a HubConnection
        // Simpler: exercise the DB guard directly (the hub path funnels through it):
        db.CompleteJob("j", "cancelled", 0, "Cancelled.");

        await Assert.That(db.GetJob("j")!.Status).IsEqualTo("completed");
    }
}
```

> `JobsHub` is not resolvable from DI directly (SignalR instantiates it per-connection). If you want the true hub path, drive `CancelJob` via a `HubConnection` (as `ArchiveHubTests` does) after seeding a completed job; otherwise the DB-guard assertion above is the meaningful regression (the hub's cancel funnels through `CompleteJob`). Pick one; don't leave a hub reference that won't resolve.

- [ ] **Step 7: Run GREEN + full suites**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/JobLifecycleDbTests/*"` → pass.
Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj` → green (`JobGuardTests` still green — the shared-list refactor is behavior-preserving).
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → green.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/AppData/JobStatuses.cs src/Arius.Api/AppData/AppDatabase.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs
git commit -m "fix(api): guard CompleteJob against clobbering terminal rows + share job-status sets"
```

---

## Task 3: Small server correctness trio — poller duration (#10), warning count (#12), dead Log send (#15)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs:394` (#10)
- Modify: `src/Arius.Api/Endpoints/JobEndpoints.cs:51` + `src/Arius.Api/Hubs/JobsHub.cs:100` (#12)
- Modify: `src/Arius.Api/Jobs/JobSink.cs:32-37` (#15)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs` (extend); `src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs` (extend)

**Interfaces:** no signature changes.

### #10 — poller-completed restore duration
`ResumeRestoreAsync` builds the terminal outcome with `BuildOutcome(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)` (`JobRunner.cs:394`) → `DurationSeconds ≈ 0`. Use the real start.

- [ ] **Step 1: Fix** — replace `JobRunner.cs:394`:

```csharp
            var startedAt = job.StartedAt ?? resume.RehydrationStartedAt;
            var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, null));
```
(`job` and `resume` are both in scope in `ResumeRestoreAsync`; `job.StartedAt` is the true job start, falling back to the rehydration start.)

### #12 — warning count undercount (>200)
`GET /jobs/{id}` uses `persisted?.Warnings.Count` (ring-capped at 200); the `/warnings` endpoint correctly uses `Snapshot.WarningCount`. `JobsHub.AttachToJob` has the same bug.

- [ ] **Step 2: Fix `JobEndpoints.cs:51`**:

```csharp
                    warningCount = persisted?.Snapshot.WarningCount ?? 0;
```

- [ ] **Step 3: Fix `JobsHub.AttachToJob:100`** — replace `persisted.Warnings.Count` with the true total:

```csharp
                    return new JobAttachState(job.Status, persisted.Snapshot, Cost: null, persisted.Snapshot.WarningCount);
```
(The `Cost: null` here is corrected in Task 4 — leave it for now.)

### #15 — dead `Log` SignalR send
`JobSink.Log` fires `Group?.SendAsync("Log", …)` but the client removed its `Log` handler (only `Progress`/`CostEstimate`/`Done` are bound). Drop the send; keep the warn/error capture.

- [ ] **Step 4: Fix `JobSink.cs:32-37`**:

```csharp
    public void Log(string text, string severity = "meta")
    {
        // Capture warn/error lines for the warnings panel/count. The live "Log" SignalR stream was removed
        // with the console (Plan 3 web cutover) — there is no client handler, so we no longer broadcast it.
        if (severity is "warn" or "error")
            CaptureWarning(text);
    }
```

- [ ] **Step 5: Tests**

Extend `src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs` — confirm `Log` still captures (WarningCount) after the send is dropped, and that >200 warnings report the true count:

```csharp
    [Test]
    public async Task Log_still_captures_warnings_after_the_live_send_is_removed()
    {
        var s = new JobSink();   // inert (Group == null)
        for (var i = 0; i < 250; i++) s.Log($"warn {i}", "warn");
        await Assert.That(s.WarningCount).IsEqualTo(250);          // true total, not ring-capped
        await Assert.That(s.Warnings.Count).IsEqualTo(200);        // ring tail still capped
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).WarningCount).IsEqualTo(250);
    }
```

Extend `LifecycleScenarioTests.cs` — a scripted restore that emits >200 warnings, park/complete it, then `GET /jobs/{id}` (via the factory's `HttpClient`) returns `warningCount == the true total` (matching `/jobs/{id}/warnings`). If wiring a >200-warning scripted scenario is heavy, assert the endpoint reads `Snapshot.WarningCount` by persisting a `PersistedJobState` with `Snapshot.WarningCount=250, Warnings=[…50 lines]` directly via `db.SaveJobState` and asserting the endpoint returns 250 (not 50).

- [ ] **Step 6: Run + commit**

Run the focused tests (`--treenode-filter`) + full `Arius.Api.Tests` + `Arius.Api.Integration.Tests` → all green.

```bash
git add src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs src/Arius.Api.Integration.Tests/LifecycleScenarioTests.cs
git commit -m "fix(api): real poller-completion duration (#10), true warning count on attach/detail (#12), drop dead Log send (#15)"
```

---

## Task 4: Reattach-state seeding — persist + surface cost / auto-resume / rehydration window (fix #2, #13, #14 server side)

**Files:**
- Modify: `src/Arius.Api/Jobs/PersistedJobState.cs` (add `Cost`)
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (`BuildPersistedState` gains a cost param)
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (persist the built `CostEstimateDto` in the awaiting-cost park branch)
- Modify: `src/Arius.Api/Contracts/JobDetailDtos.cs` (`ResumeInfo`; `JobAttachState`/`JobDetailDto` gain `Cost`/`Resume`)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`AttachToJob` surfaces cost + resume)
- Modify: `src/Arius.Api/Endpoints/JobEndpoints.cs` (`GET /jobs/{id}` surfaces cost + resume)
- Test: `src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs` (new)

**Interfaces:**
- Produces: `PersistedJobState.Cost` (`CostEstimateDto?`); `ResumeInfo(bool AutoResume, DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours)`; `JobAttachState(Status, Snapshot, Cost, WarningCount, ResumeInfo? Resume)`; `JobDetailDto(… , CostEstimateDto? Cost, ResumeInfo? Resume)`. Plan 3b's client consumes these.

**Background:** the design intends a parked restore to be re-answerable: *"the estimate is pushed for the immediate modal + persisted for 'Review cost ›'."* Today `AttachToJob` hardcodes `Cost: null` on every path and nothing persists the estimate — so "Review cost ›" can never re-render (#2). `AutoResume` and the rehydration window ARE persisted (in `RestoreResumeState`) but never surfaced to the client (#14, #13). This task persists the cost estimate and surfaces cost + a `ResumeInfo` on both reattach paths.

- [ ] **Step 1: Add `Cost` to `PersistedJobState`** (`PersistedJobState.cs:11`):

```csharp
    public          RestoreResumeState?        Resume   { get; init; }
    /// <summary>The cost estimate shown at the modal, persisted so "Review cost ›" can re-render it after the
    /// live one-shot CostEstimate message is gone (reattach to an awaiting-cost job). Null for non-cost jobs.</summary>
    public          Arius.Api.Contracts.CostEstimateDto? Cost { get; init; }
```

- [ ] **Step 2: `BuildPersistedState` accepts the cost** (`JobSink.cs:250-255`):

```csharp
    public PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume, Arius.Api.Contracts.CostEstimateDto? cost = null) => new()
    {
        Snapshot = BuildSnapshot(now),
        Warnings = Warnings,
        Resume   = resume,
        Cost     = cost,
    };
```

- [ ] **Step 3: Persist the estimate in the awaiting-cost park branch** (`JobRunner.cs`)

In `RunRestoreAsync`, capture the built DTO. In the `confirmRehydration` callback (around `:197`), after building `new CostEstimateDto(...)`, assign it to a captured local (add `CostEstimateDto? lastCostDto = null;` next to `lastEstimate` at `:183`, and set `lastCostDto = <the dto>;` where `sink.Cost(...)` is called). Then in the `costTimedOut` park branch (`:244-247`), pass it:

```csharp
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(
                    DateTimeOffset.UtcNow,
                    ResumeParamsFor(lastEstimate, version, targetPaths, destination, overwrite, noPointers,
                                    priority: "Standard", autoResume: true, startedAt: DateTimeOffset.UtcNow),
                    cost: lastCostDto)));
```

Concretely, change the `sink.Cost(...)` call site to:
```csharp
                    var costDto = new CostEstimateDto(
                        JobId: jobId,
                        ChunksAvailable:          estimate.ChunksAvailable + estimate.ChunksAlreadyRehydrated,
                        ChunksNeedingRehydration: estimate.ChunksNeedingRehydration,
                        BytesNeedingRehydration:  estimate.BytesNeedingRehydration,
                        DownloadBytes:            estimate.DownloadBytes,
                        TotalStandard:            estimate.TotalStandard,
                        TotalHigh:                estimate.TotalHigh,
                        StandardWaitHours:        estimate.StandardWait.TotalHours,
                        HighWaitHours:            estimate.HighWait.TotalHours);
                    lastCostDto = costDto;
                    sink.Cost(costDto);
```

- [ ] **Step 4: Add `ResumeInfo` + extend the DTOs** (`JobDetailDtos.cs`)

```csharp
/// <summary>The parked-restore resume facts a reattaching client needs: whether auto-resume is on, and the
/// rehydration SLA window ("≈ hydrated by" = RehydrationStartedAt + RehydrationWindowHours). Null for jobs
/// with no restore-resume state.</summary>
public sealed record ResumeInfo(bool AutoResume, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours);

public sealed record JobAttachState(string Status, Arius.Api.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount, ResumeInfo? Resume);

public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Jobs.JobSnapshot? Snapshot, int WarningCount, CostEstimateDto? Cost, ResumeInfo? Resume);
```

- [ ] **Step 5: Surface cost + resume in `AttachToJob`** (`JobsHub.cs:84-105`) — replace the method body's return paths:

```csharp
        if (jobStates.TryGet(jobId, out var sink))
            return new JobAttachState(job.Status, sink.BuildSnapshot(DateTimeOffset.UtcNow), Cost: null, sink.WarningCount, Resume: null);

        if (job.StateJson is not null)
        {
            try
            {
                var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                if (persisted is not null)
                    return new JobAttachState(job.Status, persisted.Snapshot, persisted.Cost, persisted.Snapshot.WarningCount, ToResumeInfo(persisted.Resume));
            }
            catch (System.Text.Json.JsonException) { /* fall through to a bare snapshot */ }
        }
        return new JobAttachState(job.Status, EmptySnapshot(jobId), Cost: null, WarningCount: 0, Resume: null);
```

Add a private helper to `JobsHub`:

```csharp
    private static ResumeInfo? ToResumeInfo(RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours);
```

> The live-sink path (first return) leaves `Cost: null` because a running/rehydrating job with a live sink is past the modal; a job *parked* at awaiting-cost has no live sink (the run's task is blocked, not registered as a re-attachable live sink for cost) so it takes the persisted path. Confirm this by inspecting `jobStates.TryGet` for an awaiting-cost job in the integration test (Step 8) — if a parked job DOES report a live sink, also thread `persisted.Cost` through the live path.

- [ ] **Step 6: Surface cost + resume in `GET /jobs/{id}`** (`JobEndpoints.cs:38-58`)

Add locals and populate the DTO. Replace the snapshot/warning block + the `Results.Ok(...)`:

```csharp
            JobSnapshot? snapshot = null;
            var warningCount = 0;
            CostEstimateDto? cost = null;
            ResumeInfo? resume = null;
            if (jobStates.TryGet(id, out var sink))
            {
                snapshot = sink.BuildSnapshot(DateTimeOffset.UtcNow);
                warningCount = sink.WarningCount;
            }
            else if (job.StateJson is not null)
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                    snapshot = persisted?.Snapshot;
                    warningCount = persisted?.Snapshot.WarningCount ?? 0;
                    cost = persisted?.Cost;
                    resume = persisted?.Resume is { } r ? new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours) : null;
                }
                catch (JsonException) { /* leave snapshot null */ }
            }

            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome, snapshot, warningCount, cost, resume));
```

- [ ] **Step 7: Integration test** (`src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs`, new)

Drive a restore that parks at awaiting-cost (a `RestoreScenario` with a `CostPrompt`, started via `JobRunner.RunRestoreAsync` but NOT approved), then reattach with a *fresh* `HubConnection` and assert `AttachToJob` returns a non-null `Cost` (proving "Review cost ›" can render); and `GET /jobs/{id}` returns the same cost + a `Resume` with `autoResume` and `rehydrationWindowHours`.

```csharp
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Contracts;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ReattachScenarioTests
{
    [Test]
    public async Task Awaiting_cost_job_surfaces_the_persisted_estimate_on_reattach()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 3, TotalOriginalSize: 3000),
                new ChunkResolutionCompleteEvent(TotalChunks: 5, LargeCount: 1, TarCount: 1, TotalChunkBytes: 3000),
                new RehydrationStatusEvent(Available: 3, Rehydrated: 0, NeedsRehydration: 2, Pending: 0),
            ],
            CostPrompt: new RestoreCostEstimate
            {
                ChunksAvailable = 3, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 2, ChunksPendingRehydration = 0,
                BytesNeedingRehydration = 1200, BytesPendingRehydration = 0, DownloadBytes = 3000,
                TotalStandard = 0.71, TotalHigh = 4.31, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();
        // Fire-and-forget: the run parks at awaiting-cost (no approval), persisting state_json + Cost.
        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await WaitUntil(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        // Reattach via GET /jobs/{id} — a fresh reader, no live sink for the parked job.
        var http = factory.CreateClient();
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        await Assert.That(detail.GetProperty("cost").ValueKind).IsNotEqualTo(JsonValueKind.Null);
        await Assert.That(detail.GetProperty("cost").GetProperty("totalHigh").GetDouble()).IsEqualTo(4.31);
        await Assert.That(detail.GetProperty("resume").GetProperty("autoResume").GetBoolean()).IsTrue();
        await Assert.That(detail.GetProperty("resume").GetProperty("rehydrationWindowHours").GetDouble()).IsEqualTo(15d);

        // Clean up the parked run's blocked task.
        factory.Services.GetRequiredService<RestoreApprovalRegistry>().Resolve(jobId, null);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
```

> Needs `using System.Net.Http.Json;`. If a parked awaiting-cost job unexpectedly still has a live sink (so the endpoint takes the live path and returns `cost: null`), thread `persisted.Cost` through the live path too (see Step 5 note) and re-assert. The `Resolve(jobId, null)` at the end lets the blocked `RunRestoreAsync` task finish (decline → cancelled) so the factory disposes cleanly.

- [ ] **Step 8: Run + commit**

Run: `dotnet build src/Arius.Api/Arius.Api.csproj` → clean (DTO arity changes ripple to any other `JobAttachState`/`JobDetailDto` construction — fix call sites; the web client mirror is Plan 3b).
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj --treenode-filter "/*/*/ReattachScenarioTests/*"` → pass; then full project + full `Arius.Api.Tests` → green.

```bash
git add src/Arius.Api/Jobs/PersistedJobState.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Contracts/JobDetailDtos.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs
git commit -m "feat(api): persist + surface restore cost/auto-resume/rehydration-window on reattach (#2/#13/#14)"
```

---

## Self-review

**Spec coverage:** #3 → Task 1 (atomic resolve-or-timeout). #4 → Task 2 (`CompleteJob` terminal guard + shared status set; `CancelJob` now no-ops on terminal via the guard). #10 → Task 3 (`ResumeRestoreAsync` real start). #12 → Task 3 (`GET /jobs/{id}` + `AttachToJob` use `Snapshot.WarningCount`). #15 → Task 3 (drop dead `Log` send). #2/#13/#14 server → Task 4 (persist `Cost`; surface `Cost` + `ResumeInfo` on `AttachToJob` + `GET /jobs/{id}`). The client rendering of these (#2/#7/#13/#14 UI, pill, `" photos"`) is **Plan 3b**.

**Placeholder scan:** no TBD/TODO. The `>` notes are genuine decision/verification points (whether a parked awaiting-cost job has a live sink → thread cost through the live path if so; whether to drive `CancelJob` via a `HubConnection` or assert the DB guard directly; the DTO-arity ripple to fix call sites). Each names the exact check.

**Type consistency:** `JobStatuses.{Terminal,NonTerminal,TerminalSqlList,NonTerminalSqlList}` used consistently in Task 2. `PersistedJobState.Cost` (`CostEstimateDto?`), `BuildPersistedState(now, resume, cost)`, `ResumeInfo(AutoResume, RehydrationStartedAt, RehydrationWindowHours)`, and the extended `JobAttachState`/`JobDetailDto` arities match across Tasks 2/3/4 and their construction sites (`JobsHub`, `JobEndpoints`, `JobRunner`). `BuildOutcome(startedAt, now, snapshot)` unchanged — only its argument at `JobRunner.cs:394` changes.

**Race-test caveat (Task 1):** the dropped-approval race is covered by a 300-iteration stress test asserting the honor-invariant (a fully deterministic test would need a clock seam in `RegisterAsync`, out of scope). The core fix — read the TCS result when the timeout claim fails — is the guarantee; the stress test exercises it under contention.

**Carry to Plan 3b:** client mirrors of `JobAttachState.resume`/`JobDetailDto.cost`+`resume`; render "Review cost ›" from the seeded cost (#2), `hydratedBy` from `resume.rehydrationWindowHours` (#13), the auto-resume toggle from `resume.autoResume` (#14); `jobDone` list live-update (#7); pill center-bottom; `" photos"` cleanup; Vitest setup + Codecov; the e2e control endpoint + the by-design #1 lock-in spec; and the ScriptedRestoreHandler distinct-declined-result shape carried from Plan 2.
