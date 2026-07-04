# Jobs/Progress UX — Plan 2: Api job lifecycle & realtime protocol

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a job a full, reattachable lifecycle in the Api — cancellation, a bounded cost-approval handshake, rehydration auto-resume driven by a restart-safe poller, verbatim per-job warnings, and a snapshot-on-attach protocol — all on top of Plan 1's `JobSink`/`JobStateRegistry`/`AppDatabase` foundation, with the net Core footprint held to two additive fields on the cost estimate.

**Architecture:** The live `JobSink` gains a per-job `CancellationTokenSource` and a bounded warnings ring. `JobRunner` threads that token into Core, distinguishes cancellation from failure, branches on `RestoreResult.ChunksPendingRehydration`, and persists a richer `PersistedJobState` (snapshot + warnings + restore-resume params) into the existing `state_json` column at each lifecycle transition. A `RehydrationPollingService` (mirroring `SchedulerService`) re-drives pending restores on an adaptive cadence, re-hydrating its work list from the DB so it survives restart. `JobsHub` adds attach/detach, cancel, approve/decline, and auto-resume methods; REST gains single-job read + warnings + filters. Azure rehydration SLAs (High ≈1 h, Standard ≈15 h) live once in the Azure estimator and cross the boundary via `RestoreCostEstimate`.

**Tech Stack:** .NET / C#, Mediator (`ISender.Send(cmd, ct)`), SignalR (`IHubContext<JobsHub>`, `Hub` groups), Microsoft.Data.Sqlite, `System.Text.Json`, TUnit + Shouldly.

**Design spec:** `docs/history/superpowers/2026-07-04-jobs-progress-ux-design.md` (§5 reattach, §6 guard, §7 rehydration auto-resume, §8 cost lifecycle, §9 cancellation, §12 status vocab, §13 pill/history, §14 Api surface + warnings, §18 wait windows).

**Predecessor:** Plan 1 (`docs/history/superpowers/2026-07-04-jobs-progress-ux-plan-1-foundation.md`), commits `d8f80c5c..6feeaa92`, all green.

## Global Constraints

- **Host→features boundary:** Api code must not resolve or call Core `Shared` services (`IChunkStorageService`, `ISnapshotService`, …); go through `IMediator`/features. Rehydration status is obtained by **re-triggering the restore feature**, never by a status peek into `Shared`. (AGENTS.md.)
- **Net Core footprint for the whole feature = the two Plan-1 events PLUS exactly two additive fields** on `RestoreCostEstimate` (`StandardWait`, `HighWait`). The wait *values* live in `Arius.AzureBlob` (provider SLA knowledge, ADR-0020), not in Core. No new Core events, no other Core changes.
- **Non-terminal statuses are exactly** `running`, `awaiting-cost`, `rehydrating`. Terminal: `completed`, `failed`, `cancelled`, `interrupted`. These must stay in lockstep with the `ux_jobs_one_active_per_repo` partial unique index (already created in Plan 1 over exactly this set). Do not introduce a new status.
- **Same jobId across a resume.** A parked job (awaiting-cost / rehydrating) that is approved, re-driven by the poller, or manually resumed keeps its original jobId and DB row — resume is an `UPDATE`, never a new `InsertJob`, so it is exempt from the single-active-job guard (§6).
- **Progress stays absolute-state (latest-wins).** Do **not** remove the console or the `Log`/`Cost`/`Done` SignalR messages in this plan (the current Angular drawer still consumes them until Plan 3). New payload fields are **additive** (a field the old client ignores) so the existing Web keeps working.
- **Cancellation is cooperative.** Core is already cancellation-aware; the Api's only bug is passing `CancellationToken.None`. An `OperationCanceledException` out of a command means "cancelled", never "failed".
- **Cancel never deletes rehydrated chunk copies** (§9). This plan does not pass `ConfirmCleanup`, so Core retains rehydrated copies exactly as today (reusable by a later re-run; ADR-0017 idempotent restore). Auto-cleanup-after-success is a deliberate follow-up, not this plan.
- **Cost wait is bounded to 15 minutes** (§8). An unanswered cost modal parks the job (`awaiting-cost`) and releases the repo gate; it does not hold the gate forever.
- **Tests:** TUnit. Run a class with `dotnet test --project <csproj> --treenode-filter "/*/*/<ClassName>/*"`. `Arius.Api.Tests` uses **Shouldly** (`value.ShouldBe(...)`). Follow `JobPersistenceTests.NewDatabase()` for a temp-file `AppDatabase`. Non-test classes `internal` where the codebase does; one top-level class per file; mirror source structure.
- **Domain vocabulary:** chunk, content hash, chunk hash, dedup, tar bundle, snapshot, rehydration, rehydration priority (Standard/High), byte-weighted, idempotent restore.

---

## File structure

**Core / provider (modify — the entire Core footprint of this plan):**
- `src/Arius.Core/Shared/Cost/IStorageCostEstimator.cs` — add `StandardWait`/`HighWait` (`TimeSpan`, `required`) to the `RestoreCostEstimate` record.
- `src/Arius.AzureBlob/Pricing/AzureBlobCostEstimator.cs` — populate the two new fields from a local SLA const.
- `src/Arius.Tests.Shared/Fakes/FakeStorageCostEstimator.cs` — populate the two new fields (deterministic).

**Api (create):**
- `src/Arius.Api/Jobs/PersistedJobState.cs` — the `state_json` shape: `{ Snapshot, Warnings, Resume }` + `RestoreResumeState` record.
- `src/Arius.Api/Jobs/RehydrationSchedule.cs` — pure `IsDue(...)` cadence function (testable without a clock).
- `src/Arius.Api/Jobs/RehydrationPollingService.cs` — `BackgroundService` that re-drives pending restores.
- `src/Arius.Api/Contracts/JobDetailDtos.cs` — `CostEstimateDto`, `JobAttachState`, `JobDetailDto`, `JobWarningsDto`.

**Api (modify):**
- `src/Arius.Api/Jobs/JobSink.cs` — per-job `Cts`; warnings ring fed from `Log`; `WarningCount` on the snapshot; `BuildPersistedState(...)`; stop the timer's final emit racing past `Done`.
- `src/Arius.Api/Jobs/JobSnapshot.cs` — add `WarningCount`.
- `src/Arius.Api/Jobs/JobStateRegistry.cs` — `CancelLive(jobId)` helper.
- `src/Arius.Api/Jobs/JobRunner.cs` — token threading; OCE→cancelled; cost timeout→park; rehydration branch; `ResumeRestoreAsync`; persist `PersistedJobState`; extract the shared restore body.
- `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs` — bounded-wait `RegisterAsync`; remove `CancelForConnection`.
- `src/Arius.Api/Hubs/JobsHub.cs` — `AttachToJob`/`DetachFromJob`/`CancelJob`/`ApproveRestore`/`DeclineRestore`/`SetAutoResume`/`ResumeRestore`; remove `OnDisconnectedAsync→CancelForConnection`; `Done`/`Cost` carry `jobId`.
- `src/Arius.Api/AppData/AppDatabase.cs` — `SetJobStatus`, `GetJob`, `ListActiveRehydrations`.
- `src/Arius.Api/Endpoints/JobEndpoints.cs` — `GET /jobs/{id}`, `GET /jobs/{id}/warnings`, `status`/`repositoryId` filters on `GET /jobs`.
- `src/Arius.Api/Program.cs` — register `RehydrationPollingService` hosted service.

**Tests (create):**
- `src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs`, `RehydrationScheduleTests.cs`, `RestoreApprovalRegistryTests.cs`
- `src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs`
- `src/Arius.Core.Tests/Cost/RestoreCostEstimateWaitTests.cs` (or nearest existing Core-test home — see Task 1)

---

## Task 1: Rehydration wait windows on the cost estimate

**Files:**
- Modify: `src/Arius.Core/Shared/Cost/IStorageCostEstimator.cs:72-87`
- Modify: `src/Arius.AzureBlob/Pricing/AzureBlobCostEstimator.cs:52-69`
- Modify: `src/Arius.Tests.Shared/Fakes/FakeStorageCostEstimator.cs:37-53`
- Test: `src/Arius.Core.Tests/Cost/RestoreCostEstimateWaitTests.cs`

**Interfaces:**
- Produces: `RestoreCostEstimate.StandardWait` / `.HighWait` (`TimeSpan`, `required`). The Azure estimator sets them from `AzureBlobCostEstimator.StandardRehydrationWait` (`TimeSpan.FromHours(15)`) and `HighRehydrationWait` (`TimeSpan.FromHours(1)`); the fake sets the same values so downstream tests are deterministic.

**Context:** These are Azure rehydration SLAs (objects < 10 GB: Standard ≤ 15 h, High ≤ 1 h). `RestoreCostEstimate` is a `required`-init record with exactly two constructors in the whole solution (`AzureBlobCostEstimator`, `FakeStorageCostEstimator`); both must set the new fields or the solution will not compile. Adding fields is additive to the provider-neutral contract, next to `TotalStandard`/`TotalHigh`.

- [ ] **Step 1: Confirm the Core-test home**

Run: `ls src/Arius.Core.Tests/Cost/ 2>/dev/null || ls src/Arius.Core.Tests`
If a `Cost/` folder exists, put the test there; otherwise create `src/Arius.Core.Tests/Cost/`. Use the test project's existing assertion style (check a sibling test file for TUnit `[Test]` + Shouldly-or-`Assert.That`). The test below uses `Assert.That` (portable across both).

- [ ] **Step 2: Write the failing test**

```csharp
// src/Arius.Core.Tests/Cost/RestoreCostEstimateWaitTests.cs
using Arius.Core.Shared.Cost;
using Arius.Tests.Shared.Fakes;

namespace Arius.Core.Tests.Cost;

public class RestoreCostEstimateWaitTests
{
    [Test]
    public async Task Estimate_carries_rehydration_wait_windows()
    {
        var estimator = new FakeStorageCostEstimator();
        var estimate = estimator.EstimateRestoreCost(new RestoreCostRequest
        {
            ChunksNeedingRehydration = 3,
            BytesNeedingRehydration  = 3_000_000,
            DownloadBytes            = 1_000_000,
        });

        await Assert.That(estimate.StandardWait).IsEqualTo(TimeSpan.FromHours(15));
        await Assert.That(estimate.HighWait).IsEqualTo(TimeSpan.FromHours(1));
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (record does not have the members)

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/RestoreCostEstimateWaitTests/*"`
Expected: FAIL to compile (`StandardWait`/`HighWait` do not exist).

- [ ] **Step 4: Add the two fields to the contract**

In `IStorageCostEstimator.cs`, inside `RestoreCostEstimate`, after the `TotalHigh` property (line ~86):

```csharp
    /// <summary>Provider rehydration SLA at Standard priority — the upper bound on how long archive-tier
    /// chunks take to rehydrate before their files can be restored. Lets the UI render "up to {StandardWait}"
    /// and the Api compute a "≈ hydrated by" heuristic, without any host hardcoding a provider constant.</summary>
    public required TimeSpan StandardWait { get; init; }

    /// <summary>Provider rehydration SLA at High priority (faster, costlier). See <see cref="StandardWait"/>.</summary>
    public required TimeSpan HighWait { get; init; }
```

- [ ] **Step 5: Populate them in the Azure estimator**

In `AzureBlobCostEstimator.cs`, add the SLA consts as fields (after `BytesPerGiB`, line ~16):

```csharp
    /// <summary>Azure rehydration SLA (objects &lt; 10 GB) at Standard priority. Single source of truth for the
    /// rehydration wait window; surfaced to hosts via <see cref="RestoreCostEstimate.StandardWait"/> (ADR-0020).</summary>
    public static readonly TimeSpan StandardRehydrationWait = TimeSpan.FromHours(15);

    /// <summary>Azure rehydration SLA (objects &lt; 10 GB) at High priority.</summary>
    public static readonly TimeSpan HighRehydrationWait = TimeSpan.FromHours(1);
```

Then in `EstimateRestoreCost`, add the two fields to the returned `RestoreCostEstimate` (after `TotalHigh = cost.TotalHigh,`):

```csharp
            StandardWait = StandardRehydrationWait,
            HighWait     = HighRehydrationWait,
```

- [ ] **Step 6: Populate them in the fake**

In `FakeStorageCostEstimator.cs` `EstimateRestoreCost`, add after `TotalHigh = restoredGiB + rehydrateGiB,`:

```csharp
            StandardWait = TimeSpan.FromHours(15),
            HighWait     = TimeSpan.FromHours(1),
```

- [ ] **Step 7: Run the test — expect PASS; build the affected projects**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/RestoreCostEstimateWaitTests/*"`
Then: `dotnet build src/Arius.AzureBlob` (expect clean — no other `new RestoreCostEstimate` sites exist).
Expected: PASS + clean build.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Core/Shared/Cost/IStorageCostEstimator.cs src/Arius.AzureBlob/Pricing/AzureBlobCostEstimator.cs src/Arius.Tests.Shared/Fakes/FakeStorageCostEstimator.cs src/Arius.Core.Tests/Cost/RestoreCostEstimateWaitTests.cs
git commit -m "feat(core): surface rehydration wait windows on RestoreCostEstimate"
```

---

## Task 2: `AppDatabase` job-lifecycle helpers

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs` (Jobs region, ~line 366-466)
- Test: `src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs`

**Interfaces:**
- Produces: `void SetJobStatus(string id, string status, string? detail = null)` (updates `status` + optional `detail`, **does not** set `finished_at` — for non-terminal transitions); `JobRecord? GetJob(string id)`; `IReadOnlyList<JobRecord> ListActiveRehydrations()` (rows with `status = 'rehydrating'`, for the poller).
- Consumes: existing `InsertJob`, `CompleteJob`, `HasActiveJob`, `ReadJob`, `JobRecord` (Plan 1).

**Context:** Plan 1 has `InsertJob` (status='running', started_at set) and `CompleteJob` (terminal: sets status/pct/detail/finished_at). Non-terminal transitions (`running`→`awaiting-cost`, `running`→`rehydrating`, and the poller's `rehydrating`→`running` re-drive) need a status update that leaves `finished_at` NULL. A single-row read (`GetJob`) backs `GET /jobs/{id}` and the poller. `ReadJob(SqliteDataReader)` already reads all 11 columns including `state_json`/`outcome`.

- [ ] **Step 1: Write the failing test**

```csharp
// src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs
using Arius.Api.AppData;
using Shouldly;

namespace Arius.Api.Tests.AppData;

public sealed class JobLifecycleDbTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        return (database, repositoryId);
    }

    [Test]
    public async Task SetJobStatus_updates_status_without_finishing()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j1", repoId, "restore", "one-off", "running");

        db.SetJobStatus("j1", "rehydrating", "Waiting for rehydration");

        var job = db.GetJob("j1")!;
        job.Status.ShouldBe("rehydrating");
        job.Detail.ShouldBe("Waiting for rehydration");
        job.FinishedAt.ShouldBeNull();
        db.HasActiveJob(repoId).ShouldBeTrue();   // rehydrating is non-terminal
    }

    [Test]
    public async Task GetJob_returns_null_for_unknown_id()
    {
        var (db, _) = NewDatabase();
        db.GetJob("nope").ShouldBeNull();
    }

    [Test]
    public async Task ListActiveRehydrations_returns_only_rehydrating_rows()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j1", repoId, "restore", "one-off", "running");
        db.SetJobStatus("j1", "rehydrating");

        var acc2 = db.InsertAccount("acc2", null);
        var repo2 = db.InsertRepository("a2", "c2", acc2, null, "archive", null);
        db.InsertJob("j2", repo2, "restore", "one-off", "running");   // stays running

        var rows = db.ListActiveRehydrations();
        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe("j1");
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobLifecycleDbTests/*"`
Expected: FAIL (methods missing).

- [ ] **Step 3: Add the three methods**

In `AppDatabase.cs`, in the `// ── Jobs ──` region (e.g. right after `CompleteJob`, line ~396):

```csharp
    /// <summary>Updates a job's <c>status</c> (and optional <c>detail</c>) for a NON-terminal transition
    /// (running↔awaiting-cost↔rehydrating). Leaves <c>finished_at</c> untouched — use <see cref="CompleteJob"/>
    /// for terminal states. The <c>ux_jobs_one_active_per_repo</c> index is enforced: moving between two
    /// non-terminal statuses for the same repository's single active row is a plain UPDATE and never conflicts.</summary>
    public void SetJobStatus(string id, string status, string? detail = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = detail is null
            ? "UPDATE jobs SET status = $status WHERE id = $id;"
            : "UPDATE jobs SET status = $status, detail = $detail WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        if (detail is not null) command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    /// <summary>Reads a single job by id, or <c>null</c> if it does not exist. Backs <c>GET /jobs/{id}</c>
    /// and the rehydration poller's per-job due check.</summary>
    public JobRecord? GetJob(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>All jobs currently in <c>rehydrating</c> — the rehydration poller's work list. Rebuilt from the
    /// DB every tick so the poller holds no per-job timers and survives an Api restart (design §7).</summary>
    public IReadOnlyList<JobRecord> ListActiveRehydrations()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE status = 'rehydrating';";
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobLifecycleDbTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/AppData/AppDatabase.cs src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs
git commit -m "feat(api): add SetJobStatus / GetJob / ListActiveRehydrations lifecycle helpers"
```

---

## Task 3: `JobSink` warnings ring + `PersistedJobState`

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs`
- Modify: `src/Arius.Api/Jobs/JobSnapshot.cs` (add `WarningCount`)
- Create: `src/Arius.Api/Jobs/PersistedJobState.cs`
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (switch the two completion `SaveJobState` calls to persist `PersistedJobState`)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs`

**Interfaces:**
- Produces on `JobSink`: warnings are captured automatically inside `Log(text, severity)` when `severity` is `"warn"` or `"error"`; `int WarningCount { get; }` (total ever seen, accurate even after the ring trims); `IReadOnlyList<string> Warnings { get; }` (last ≤ 200, oldest→newest); `PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume)`.
- Produces: `PersistedJobState { JobSnapshot Snapshot; IReadOnlyList<string> Warnings; RestoreResumeState? Resume; }`; `RestoreResumeState { string? Version; IReadOnlyList<string> TargetPaths; string Destination; bool Overwrite; bool NoPointers; string Priority; bool AutoResume; DateTimeOffset RehydrationStartedAt; DateTimeOffset LastRunAt; TimeSpan RehydrationWindow; int AvailableOrRehydratedCount; }`.
- `JobSnapshot` gains `public required int WarningCount { get; init; }`.
- Consumes: Plan 1's `JobSnapshot`, `JobSink.Log`, `JobSink.BuildSnapshot`, `JobRunner`'s completion path.

**Context:** Plan 1's `state_json` stores a bare serialized `JobSnapshot`. From this task on it stores a `PersistedJobState` (snapshot + warnings tail + optional restore-resume params). Nothing reads `state_json` yet (Plan 1 only wrote it), so changing its shape is safe. Warnings are the operationally-meaningful lines the forwarders already emit at `"warn"`/`"error"` severity (rehydration-needed, skipped files, `ex.Message` on failure), captured verbatim so they can be consulted after restart (design §14 — the user's requirement: "consult the warnings per job afterwards, after a restart of the api"). Live count rides `Progress.warningCount`; the verbatim lines are fetched lazily via REST (Task 9).

- [ ] **Step 1: Write the failing warnings test**

```csharp
// src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs
using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class JobSinkWarningsTests
{
    [Test]
    public async Task Warn_and_error_logs_are_captured_verbatim_info_is_not()
    {
        var s = new JobSink();
        s.Log("scanning…", "meta");
        s.Log("archive-tier chunks need rehydration", "warn");
        s.Log("all good", "info");
        s.Log("disk write failed", "error");

        s.WarningCount.ShouldBe(2);
        s.Warnings.ShouldBe(new[] { "archive-tier chunks need rehydration", "disk write failed" });
    }

    [Test]
    public async Task Warning_ring_is_bounded_but_count_stays_accurate()
    {
        var s = new JobSink();
        for (var i = 0; i < 250; i++) s.Log($"warn {i}", "warn");

        s.WarningCount.ShouldBe(250);          // total ever, not the ring size
        s.Warnings.Count.ShouldBe(200);        // ring cap
        s.Warnings[^1].ShouldBe("warn 249");   // newest retained
        s.Warnings[0].ShouldBe("warn 50");     // oldest 50 trimmed
    }

    [Test]
    public async Task Snapshot_reports_warning_count()
    {
        var s = new JobSink();
        s.Log("w1", "warn");
        s.BuildSnapshot(DateTimeOffset.UnixEpoch).WarningCount.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkWarningsTests/*"`
Expected: FAIL (no `WarningCount`/`Warnings`; `JobSnapshot.WarningCount` missing).

- [ ] **Step 3: Add the warnings ring to `JobSink`**

In `JobSink.cs`, replace the `Log` method (line ~27) with a capturing version and add the ring state + accessors next to it:

```csharp
    // ── Messages ────────────────────────────────────────────────────────────
    public void Log(string text, string severity = "meta")
    {
        if (severity is "warn" or "error")
            CaptureWarning(text);
        Group?.SendAsync("Log", new { text, severity });
    }
    public void Cost(object estimate) => Group?.SendAsync("CostEstimate", estimate);
    public void Done(string status, string summary) => Group?.SendAsync("Done", new { status, summary });

    // ── Warnings capture (verbatim warn/error lines; count survives ring trimming) ──
    private const int WarningRingCap = 200;
    private readonly LinkedList<string> _warnings = new();
    private readonly object _warnLock = new();
    private int _warningCount;

    private void CaptureWarning(string text)
    {
        lock (_warnLock)
        {
            _warningCount++;
            _warnings.AddLast(text);
            if (_warnings.Count > WarningRingCap) _warnings.RemoveFirst();
        }
    }

    /// <summary>Total warn/error lines seen (accurate even after the ring trims older lines).</summary>
    public int WarningCount { get { lock (_warnLock) return _warningCount; } }

    /// <summary>The last ≤200 warn/error lines, oldest→newest.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_warnLock) return _warnings.ToArray(); } }
```

- [ ] **Step 4: Add `WarningCount` to `JobSnapshot` and populate it**

In `JobSnapshot.cs`, add to the `JobSnapshot` record (after `Pct`):

```csharp
    public required int WarningCount { get; init; }
```

In `JobSink.BuildSnapshot` (JobSink.cs), add to the returned `new JobSnapshot { … }` initializer (e.g. after `Pct = pct,`):

```csharp
            WarningCount = WarningCount,
```

- [ ] **Step 5: Run the warnings tests — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkWarningsTests/*"`
Expected: PASS.

- [ ] **Step 6: Create `PersistedJobState` + `RestoreResumeState`**

```csharp
// src/Arius.Api/Jobs/PersistedJobState.cs
namespace Arius.Api.Jobs;

/// <summary>The serialized shape of the <c>jobs.state_json</c> column: the last-known progress snapshot, the
/// verbatim warnings tail, and (restore only) the parameters needed to re-drive a parked/rehydrating job with
/// the SAME jobId. Written at each lifecycle transition (awaiting-cost, rehydrating, completion); read by
/// <c>GET /jobs/{id}</c>, snapshot-on-attach for a non-live job, and the rehydration poller. (Design §4, §7, §14.)</summary>
public sealed record PersistedJobState
{
    public required JobSnapshot               Snapshot { get; init; }
    public required IReadOnlyList<string>      Warnings { get; init; }
    public          RestoreResumeState?        Resume   { get; init; }
}

/// <summary>Everything the poller / approval fallback needs to re-run a restore with the original intent — no
/// prompt, no re-charge, no client connection (design §7, §8). The window is the chosen priority's rehydration
/// SLA captured from the cost estimate at approval time; "≈ hydrated by" = <see cref="RehydrationStartedAt"/> +
/// <see cref="RehydrationWindow"/>.</summary>
public sealed record RestoreResumeState
{
    public          string?                    Version                    { get; init; }
    public required IReadOnlyList<string>      TargetPaths                { get; init; }
    public required string                     Destination                { get; init; }
    public          bool                       Overwrite                  { get; init; }
    public          bool                       NoPointers                 { get; init; }
    public required string                     Priority                   { get; init; }   // "Standard" | "High"
    public          bool                       AutoResume                 { get; init; } = true;
    public required DateTimeOffset             RehydrationStartedAt       { get; init; }
    public required DateTimeOffset             LastRunAt                  { get; init; }
    public required TimeSpan                    RehydrationWindow          { get; init; }
    public          int                        AvailableOrRehydratedCount { get; init; }
}
```

- [ ] **Step 7: Add `BuildPersistedState` to `JobSink`**

In `JobSink.cs`, after `BuildOutcome` (line ~167):

```csharp
    /// <summary>Assembles the <see cref="PersistedJobState"/> written to <c>state_json</c> — current snapshot,
    /// warnings tail, and (restore) the resume params. Archive jobs pass <paramref name="resume"/> = null.</summary>
    public PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume) => new()
    {
        Snapshot = BuildSnapshot(now),
        Warnings = Warnings,
        Resume   = resume,
    };
```

- [ ] **Step 8: Switch the two completion persist calls in `JobRunner`**

In `JobRunner.cs`, the archive success branch currently does
`database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildSnapshot(DateTimeOffset.UtcNow)));` (line ~93). Replace with:

```csharp
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
```

In the restore success branch (line ~210), replace the equivalent line with:

```csharp
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
```

(The restore-with-pending path that persists a non-null `resume` is added in Task 6; the plain-success path is resume-null.)

- [ ] **Step 9: Build + commit**

Run: `dotnet build src/Arius.Api` (expect clean) then `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkWarningsTests/*"`.
Expected: clean build + PASS.

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobSnapshot.cs src/Arius.Api/Jobs/PersistedJobState.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs
git commit -m "feat(api): capture verbatim per-job warnings + PersistedJobState state_json shape"
```

---

## Task 4: Per-job cancellation

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (per-job `Cts`)
- Modify: `src/Arius.Api/Jobs/JobStateRegistry.cs` (`CancelLive`)
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (thread token; OCE→cancelled; dispose Cts)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`CancelJob`)
- Test: `src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs` (extend)

**Interfaces:**
- Produces on `JobSink`: `CancellationTokenSource Cts { get; }` (a fresh CTS per real sink; harmless on an inert sink).
- Produces on `JobStateRegistry`: `bool CancelLive(string jobId)` — cancels the live job's `Cts` and returns `true` if a live job was found, else `false`.
- Produces on `JobsHub`: `Task CancelJob(string jobId)`.
- Consumes: Plan 1's `JobStateRegistry.TryGet`, `JobRunner`'s `CreateJobProviderAsync(…, ct)` + `mediator.Send`.

**Context:** Core is cancellation-aware; the Api's only defect is passing `CancellationToken.None` into `CreateJobProviderAsync` and omitting the token on `mediator.Send`. A cancel of a *live* job cancels its `Cts`; the command throws `OperationCanceledException`, which `JobRunner` must map to `cancelled` (not `failed`). A cancel of a *parked* job (no live sink — e.g. a `rehydrating` job between poller re-drives, or a job orphaned by restart) is handled in Task 6/8's hub wiring; this task covers the live path + the hub entry point delegating to it. The archive snapshot is Core's last stage, so cancelling mid-upload publishes no snapshot and leaves reusable orphan chunks (safe, §9).

- [ ] **Step 1: Extend the registry test**

Append to `src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs`:

```csharp
    [Test]
    public async Task CancelLive_cancels_a_registered_jobs_token_and_reports_presence()
    {
        var reg  = new JobStateRegistry();
        var sink = new JobSink();
        reg.Register("job-1", sink);

        (await Task.FromResult(reg.CancelLive("job-1"))).ShouldBeTrue();
        sink.Cts.IsCancellationRequested.ShouldBeTrue();

        reg.CancelLive("absent").ShouldBeFalse();
    }
```

(Add `using Shouldly;` to the file if it is not already present.)

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobStateRegistryTests/*"`
Expected: FAIL (`Cts` / `CancelLive` missing).

- [ ] **Step 3: Add `Cts` to `JobSink`**

In `JobSink.cs`, add a field near `JobId` (after line ~19):

```csharp
    /// <summary>Per-job cancellation source. <see cref="CancelJob"/> cancels this; <see cref="JobRunner"/>
    /// threads its token into the Core command. A fresh source per sink (including inert sinks, where it is
    /// simply never observed).</summary>
    public CancellationTokenSource Cts { get; } = new();
```

- [ ] **Step 4: Add `CancelLive` to the registry**

In `JobStateRegistry.cs`:

```csharp
    /// <summary>Cancels a live job's token if it is currently registered. Returns whether a live job was found —
    /// a caller uses <c>false</c> to fall back to the parked-job path (mark cancelled in the DB, disarm the poller).</summary>
    public bool CancelLive(string jobId)
    {
        if (_sinks.TryGetValue(jobId, out var sink)) { sink.Cts.Cancel(); return true; }
        return false;
    }
```

- [ ] **Step 5: Thread the token + distinguish cancellation in `JobRunner`**

In `RunArchiveAsync`, change the provider + send calls to use `sink.Cts.Token`:

```csharp
            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadWrite, sink, sink.Cts.Token);
            var mediator = provider.GetRequiredService<IMediator>();

            var uploadTier = Enum.TryParse<BlobTier>(tier, ignoreCase: true, out var bt) ? bt : BlobTier.Archive;
            sink.Log($"Scanning {repo.LocalPath} …", "meta");

            var result = await mediator.Send(new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = repo.LocalPath!,
                UploadTier    = uploadTier,
                RemoveLocal   = removeLocal,
                WritePointers = writePointers,
                FastHash      = fastHash,
            }), sink.Cts.Token);
```

Add a cancellation-specific catch **before** the existing `catch (Exception ex)` in `RunArchiveAsync`:

```csharp
        catch (OperationCanceledException)
        {
            logger.LogInformation("Archive job {JobId} cancelled", jobId);
            database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
            sink.Done("cancelled", "Cancelled.");
        }
```

In `RunRestoreAsync`, change `CreateJobProviderAsync(..., CancellationToken.None)` to `sink.Cts.Token`, add `sink.Cts.Token` as the second argument to each `mediator.Send(new RestoreCommand(...))`, and add the same `catch (OperationCanceledException)` block before its `catch (Exception ex)` (with "Restore" wording).

Dispose the CTS in **both** `finally` blocks, after `jobStates.Remove(jobId);`:

```csharp
            sink.Cts.Dispose();
```

- [ ] **Step 6: Add `CancelJob` to `JobsHub`**

In `JobsHub.cs`, inject the registry (add `JobStateRegistry jobStates` to the primary constructor parameter list, after `RestoreApprovalRegistry approvals`), and add:

```csharp
    /// <summary>Requests cancellation of a live job (cooperative — takes effect at the next checkpoint). The
    /// parked-job path (mark cancelled, disarm poller/approval) is handled by the richer wiring added alongside
    /// approve/decline and auto-resume; for a live job this cancels its token.</summary>
    public Task CancelJob(string jobId)
    {
        jobStates.CancelLive(jobId);
        return Task.CompletedTask;
    }
```

(The parked-job branch is completed in Task 6 Step 8 once `DeclineRestore`/poller-disarm exist; leaving live-only here keeps this task's surface honest and testable.)

- [ ] **Step 7: Run + build**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobStateRegistryTests/*"` then `dotnet build src/Arius.Api`.
Expected: PASS + clean build.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobStateRegistry.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs
git commit -m "feat(api): per-job cancellation token + OperationCanceled→cancelled"
```

---

## Task 5: Bounded cost-approval handshake + approve/decline

**Files:**
- Modify: `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs`
- Create: `src/Arius.Api/Contracts/JobDetailDtos.cs` (introduce `CostEstimateDto` here; the rest of the file is filled in Task 8)
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (`RunRestoreAsync` cost callback: push `CostEstimateDto`, bounded wait, park on timeout)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`ApproveRestore`/`DeclineRestore`; remove `OnDisconnectedAsync→CancelForConnection`; `Approve` delegates)
- Test: `src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs`

**Interfaces:**
- Produces on `RestoreApprovalRegistry`: `Task<ApprovalResult> RegisterAsync(string jobId, TimeSpan timeout, CancellationToken ct)`; `void Resolve(string jobId, RehydratePriority? priority)` (unchanged); record `ApprovalResult(bool Approved, RehydratePriority? Priority, bool TimedOut)`. **Removed:** `CancelForConnection`, the `connectionId` parameter, and the `_ownerByJob` map.
- Produces: `CostEstimateDto` (record; see Step 4) — the `Cost` message payload and the attach-envelope cost.
- Produces on `JobsHub`: `void ApproveRestore(string jobId, string? priority)`, `void DeclineRestore(string jobId)`; `Approve` retained as a delegating alias.
- Consumes: Task 1's `RestoreCostEstimate.StandardWait`/`HighWait`; Task 2's `SetJobStatus`; Task 3's `BuildPersistedState`; `RehydratePriority` (`Arius.Core.Shared.Storage`).

**Context:** Today the callback blocks on `approvals.Register(jobId, connectionId)` with no timeout, and a dropped connection declines it (`OnDisconnectedAsync`). Design §8 changes this: the happy path is the user waiting at the keyboard and answering in the same run (unchanged mechanics); the fixes are (a) a **15-minute bound** so an abandoned modal parks the job and frees the repo gate instead of holding it forever, and (b) **removing disconnect-decline** (any connection may now answer, so a closed tab must not cancel). The full park-and-re-trigger *fallback* (restart / late answer → `ResumeRestoreAsync`) lands in Task 6; this task delivers the in-run bounded wait and marks `awaiting-cost` on timeout.

- [ ] **Step 1: Write the failing approval-registry test**

```csharp
// src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs
using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class RestoreApprovalRegistryTests
{
    [Test]
    public async Task Resolve_completes_the_wait_with_the_chosen_priority()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", RehydratePriority.High);
        var result = await waiting;

        result.Approved.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.Priority.ShouldBe(RehydratePriority.High);
    }

    [Test]
    public async Task Resolve_null_is_a_decline()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", null);
        var result = await waiting;

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeFalse();
    }

    [Test]
    public async Task Unanswered_wait_times_out()
    {
        var reg = new RestoreApprovalRegistry();
        var result = await reg.RegisterAsync("j1", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/RestoreApprovalRegistryTests/*"`
Expected: FAIL (`RegisterAsync`/`ApprovalResult` missing).

- [ ] **Step 3: Rewrite `RestoreApprovalRegistry`**

```csharp
// src/Arius.Api/Jobs/RestoreApprovalRegistry.cs
using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Jobs;

/// <summary>Outcome of a cost-approval wait: approved with a priority, explicitly declined, or timed out
/// (an abandoned modal → the job parks at <c>awaiting-cost</c> and the repo gate is released; design §8).</summary>
public sealed record ApprovalResult(bool Approved, RehydratePriority? Priority, bool TimedOut);

/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback until the client answers the cost modal
/// (<c>JobsHub.ApproveRestore</c>/<c>DeclineRestore</c>) or a bounded timeout elapses. Keyed by jobId, so ANY
/// connection may answer (a closed tab no longer declines — the owner map and <c>CancelForConnection</c> of the
/// pre-rework design are gone). <c>null</c> = decline.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Awaited by the restore command. Completes when the client approves/declines, or reports a timeout
    /// after <paramref name="timeout"/> (or if <paramref name="ct"/> is cancelled — treated as a timeout so the
    /// caller parks rather than crashing). Always removes its own pending entry.</summary>
    public async Task<ApprovalResult> RegisterAsync(string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
                return new ApprovalResult(Approved: false, Priority: null, TimedOut: true);

            var priority = await tcs.Task.ConfigureAwait(false);
            return new ApprovalResult(Approved: priority is not null, Priority: priority, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            return new ApprovalResult(Approved: false, Priority: null, TimedOut: true);
        }
        finally
        {
            _pending.TryRemove(jobId, out _);
        }
    }

    /// <summary>Completes the pending approval for a job (priority to proceed, or <c>null</c> to decline). No-op
    /// if nothing is waiting (e.g. the wait already timed out, or the run is parked after a restart — the caller
    /// then routes to the re-trigger fallback).</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryGetValue(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    /// <summary>Whether a live wait is currently parked for this job (lets the hub choose in-run resolve vs.
    /// the restart/late-answer re-trigger fallback).</summary>
    public bool HasPending(string jobId) => _pending.ContainsKey(jobId);
}
```

- [ ] **Step 4: Add `CostEstimateDto`**

```csharp
// src/Arius.Api/Contracts/JobDetailDtos.cs
namespace Arius.Api.Contracts;

/// <summary>The cost-modal payload pushed on the <c>CostEstimate</c> message and returned by snapshot-on-attach
/// for an <c>awaiting-cost</c> job. jobId-tagged so a client attached to several jobs routes it (design §5, §8).
/// Wait windows are the provider SLAs surfaced via <c>RestoreCostEstimate</c> (Task 1) — the modal renders
/// "up to {standardWaitHours} h".</summary>
public sealed record CostEstimateDto(
    string JobId,
    int    ChunksAvailable,
    int    ChunksNeedingRehydration,
    long   BytesNeedingRehydration,
    long   DownloadBytes,
    double TotalStandard,
    double TotalHigh,
    double StandardWaitHours,
    double HighWaitHours);
```

- [ ] **Step 5: Rework the cost callback in `RunRestoreAsync`**

Replace the entire `ConfirmRehydration = async (estimate, ct) => { … }` lambda (JobRunner.cs, lines ~181-198) with the bounded, jobId-tagged, parking version. Also add, just before the `foreach (var target in targets)` loop, a run-scoped record of the cost outcome:

```csharp
            // Cost-approval outcome for this run (set inside the ConfirmRehydration callback). The happy path is
            // in-run: the user answers within the window and the chosen priority feeds back into THIS run. On
            // timeout the job parks at awaiting-cost (gate released); on decline it is cancelled. (Design §8.)
            RehydratePriority? runApprovedPriority = null;
            var costDeclined = false;
            var costTimedOut = false;
            RestoreCostEstimate? lastEstimate = null;
```

Callback (inside the `RestoreOptions` initializer):

```csharp
                    ConfirmRehydration = async (estimate, ct) =>
                    {
                        // Same priority for the whole run — a later target that also needs rehydration must not
                        // re-prompt (idempotent restore reuses the already-approved answer).
                        if (runApprovedPriority is not null) return runApprovedPriority;

                        lastEstimate = estimate;
                        sink.Log(estimate.ChunksNeedingRehydration > 0
                            ? "⚠ archive-tier chunks need rehydration — awaiting cost approval"
                            : "Estimated restore cost — awaiting approval", "warn");
                        sink.Cost(new CostEstimateDto(
                            JobId: jobId,
                            ChunksAvailable:          estimate.ChunksAvailable + estimate.ChunksAlreadyRehydrated,
                            ChunksNeedingRehydration: estimate.ChunksNeedingRehydration,
                            BytesNeedingRehydration:  estimate.BytesNeedingRehydration,
                            DownloadBytes:            estimate.DownloadBytes,
                            TotalStandard:            estimate.TotalStandard,
                            TotalHigh:                estimate.TotalHigh,
                            StandardWaitHours:        estimate.StandardWait.TotalHours,
                            HighWaitHours:            estimate.HighWait.TotalHours));

                        database.SetJobStatus(jobId, "awaiting-cost", "Awaiting cost approval");

                        var answer = await approvals.RegisterAsync(jobId, TimeSpan.FromMinutes(15), ct);
                        if (answer.Approved)
                        {
                            runApprovedPriority = answer.Priority;
                            database.SetJobStatus(jobId, "running");
                            sink.Log($"Approved · {answer.Priority} priority", "info");
                            return answer.Priority;
                        }

                        costTimedOut = answer.TimedOut;
                        costDeclined = !answer.TimedOut;
                        sink.Log(answer.TimedOut ? "Cost approval timed out — parked." : "Restore declined.", "warn");
                        return null;   // Core exits with ChunksPendingRehydration = the still-needed count
                    },
```

Then, immediately **after** the `foreach` loop ends and **before** the existing `database.CompleteJob(jobId, "completed", …)` block, add the decline/timeout branches (the rehydration-pending branch is added in Task 6, which will also gate the "completed" write):

```csharp
            if (costDeclined)
            {
                database.CompleteJob(jobId, "cancelled", 0, "Restore declined at cost approval.");
                sink.Done("cancelled", "Restore declined.");
                return;
            }
            if (costTimedOut)
            {
                // Parked at awaiting-cost (status already set in the callback). Persist resume params so a later
                // ApproveRestore can re-trigger with the SAME jobId (fallback path, Task 6). Do NOT send Done —
                // the job is non-terminal. The finally block releases the repo gate.
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(
                    DateTimeOffset.UtcNow,
                    ResumeParamsFor(lastEstimate, version, targetPaths, destination, overwrite, noPointers,
                                    priority: "Standard", autoResume: true, startedAt: DateTimeOffset.UtcNow))));
                return;
            }
```

Add a small private helper to `JobRunner` (near the bottom of the class) that builds `RestoreResumeState` — Task 6 reuses it:

```csharp
    private static RestoreResumeState ResumeParamsFor(
        Arius.Core.Shared.Cost.RestoreCostEstimate? estimate,
        string? version, IReadOnlyList<string> targetPaths, string destination,
        bool overwrite, bool noPointers, string priority, bool autoResume, DateTimeOffset startedAt)
    {
        var window = priority == "High"
            ? estimate?.HighWait     ?? TimeSpan.FromHours(1)
            : estimate?.StandardWait ?? TimeSpan.FromHours(15);
        return new RestoreResumeState
        {
            Version              = version,
            TargetPaths          = targetPaths,
            Destination          = destination,
            Overwrite            = overwrite,
            NoPointers           = noPointers,
            Priority             = priority,
            AutoResume           = autoResume,
            RehydrationStartedAt = startedAt,
            LastRunAt            = startedAt,
            RehydrationWindow    = window,
        };
    }
```

(`RunRestoreAsync` already has `version`, `targetPaths`, `destination`, `overwrite`, `noPointers` in scope. Add `using Arius.Core.Shared.Cost;` if not present.)

- [ ] **Step 6: Update `JobsHub` — approve/decline + drop disconnect-decline**

In `JobsHub.cs`, delete the `OnDisconnectedAsync` override entirely (a dropped connection must no longer decline). Replace the existing `Approve` method with:

```csharp
    /// <summary>Answers the restore cost modal for a LIVE, in-run approval wait: "standard"/"high" to proceed,
    /// anything else to decline. The parked/restart fallback (re-trigger a fresh run) is <see cref="ApproveRestore"/>.</summary>
    public void ApproveRestore(string jobId, string? priority)
    {
        RehydratePriority? chosen = priority?.ToLowerInvariant() switch
        {
            "standard" => RehydratePriority.Standard,
            "high"     => RehydratePriority.High,
            _          => null,
        };

        if (approvals.HasPending(jobId))
        {
            approvals.Resolve(jobId, chosen);   // in-run: feeds back into the same RestoreCommand
            return;
        }
        // Parked (timed out / restarted): re-trigger handled in Task 6 (ResumeRestoreAsync). Until then this is
        // a no-op for a non-live job; Task 6 replaces this branch with the re-trigger call.
    }

    /// <summary>Declines the restore cost modal (equivalent to answering "cancel").</summary>
    public void DeclineRestore(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return; }
        // Parked decline is completed in Task 6 (mark cancelled + disarm).
    }

    /// <summary>Back-compat alias for the current Angular drawer; delegates to <see cref="ApproveRestore"/>.
    /// Removed when the drawer is reworked in Plan 3.</summary>
    public void Approve(string jobId, string? priority) => ApproveRestore(jobId, priority);
```

- [ ] **Step 7: Run + build**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/RestoreApprovalRegistryTests/*"` then `dotnet build src/Arius.Api`.
Expected: PASS + clean build.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/Jobs/RestoreApprovalRegistry.cs src/Arius.Api/Contracts/JobDetailDtos.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api.Tests/Jobs/RestoreApprovalRegistryTests.cs
git commit -m "feat(api): bounded jobId-keyed cost approval; park on timeout; drop disconnect-decline"
```

---

## Task 6: Rehydration branch + `ResumeRestoreAsync` + auto-resume controls

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (branch on `ChunksPendingRehydration`; `ResumeRestoreAsync`; extract shared restore body)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`SetAutoResume`, `ResumeRestore`; finish the parked branches of `ApproveRestore`/`DeclineRestore`/`CancelJob`)
- Modify: `src/Arius.Api/Jobs/RestoreForwarders.cs` (feed `AvailableOrRehydratedCount` — via the existing `SetRehydration`, already wired; no change needed beyond confirming)
- Test: `src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs` is Task 7; this task is covered by build + the existing restore integration path. Add a focused unit test for the resume-params round-trip.

**Interfaces:**
- Produces on `JobRunner`: `Task ResumeRestoreAsync(string jobId)` — re-drives a parked/rehydrating restore with the SAME jobId, non-prompting (returns the persisted priority), branching on pending again. `Task ApproveAndResumeAsync(string jobId, RehydratePriority priority)` — the restart/late-answer fallback (persist priority, then `ResumeRestoreAsync`).
- Produces on `JobsHub`: `Task SetAutoResume(string jobId, bool autoResume)`, `Task ResumeRestore(string jobId)`.
- Consumes: Task 2 (`SetJobStatus`, `GetJob`), Task 3 (`PersistedJobState`, `RestoreResumeState`, `BuildPersistedState`), Task 5 (`ResumeParamsFor`, `RehydratePriority`), Plan 1 (`RestoreResult.ChunksPendingRehydration`, gate/registry lifecycle).

**Context:** Today `RunRestoreAsync` marks `completed` unconditionally, silently dropping `ChunksPendingRehydration` (§7 latent bug). The fix: after the target loop, if the summed pending > 0 → `rehydrating` + persist resume params (poller re-drives); else → `completed`. `ResumeRestoreAsync` recreates a `JobSink` under the same jobId, re-runs the restore for the persisted targets with a **non-prompting** `ConfirmRehydration` (returns the persisted priority — honors the original Standard/High choice, no re-charge, no connection), and re-branches. It is exempt from the guard (it updates the existing row via `SetJobStatus`, never `InsertJob`). Because both the initial run and the resume share the download/branch logic, extract a private `RunRestoreOnceAsync(...)` returning the total pending count; the two public entry points wrap it with their own prompting vs non-prompting callback and status bookkeeping.

- [ ] **Step 1: Write the failing resume-params round-trip test**

```csharp
// src/Arius.Api.Tests/Jobs/PersistedJobStateTests.cs
using System.Text.Json;
using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class PersistedJobStateTests
{
    [Test]
    public async Task Resume_params_round_trip_through_state_json()
    {
        var sink = new JobSink();
        sink.SetRestoreTotals(files: 5, bytes: 5000);
        var resume = new RestoreResumeState
        {
            Version = "v3", TargetPaths = new[] { "docs" }, Destination = "/data",
            Overwrite = false, NoPointers = true, Priority = "High", AutoResume = true,
            RehydrationStartedAt = DateTimeOffset.UnixEpoch, LastRunAt = DateTimeOffset.UnixEpoch,
            RehydrationWindow = TimeSpan.FromHours(1), AvailableOrRehydratedCount = 2,
        };

        var json = JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UnixEpoch, resume));
        var back = JsonSerializer.Deserialize<PersistedJobState>(json)!;

        back.Resume.ShouldNotBeNull();
        back.Resume!.Priority.ShouldBe("High");
        back.Resume.TargetPaths.ShouldBe(new[] { "docs" });
        back.Resume.RehydrationWindow.ShouldBe(TimeSpan.FromHours(1));
        back.Snapshot.RestoreTotalFiles.ShouldBe(5L);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (compiles once Task 3 landed; this asserts serialization shape)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/PersistedJobStateTests/*"`
Expected: PASS if Task 3 is complete and STJ handles the records (it does). If it FAILS on `TimeSpan`, switch `RehydrationWindow` to `long RehydrationWindowSeconds` in `RestoreResumeState` and adjust `ResumeParamsFor`/consumers — but .NET's `System.Text.Json` serializes `TimeSpan` as ISO-8601 by default, so this should pass. (This step verifies the shape before Task 6's logic depends on it.)

- [ ] **Step 3: Extract the shared restore body in `JobRunner`**

Refactor `RunRestoreAsync` so the provider-create + target loop + pending-summation lives in a private method that both entry points call. Replace the body between `await gate.WaitAsync();` and the `finally` with a call into:

```csharp
    /// <summary>Runs the restore command over the resolved targets with the supplied cost callback, summing
    /// <see cref="RestoreResult.ChunksPendingRehydration"/>. Returns (totalPending, success, error). Shared by the
    /// initial run and <see cref="ResumeRestoreAsync"/>. The caller owns status bookkeeping + persistence.</summary>
    private async Task<(int Pending, bool Success, string? Error)> RunRestoreOnceAsync(
        ServiceProvider provider, JobSink sink, string jobId, string destination, string? version,
        IReadOnlyList<string> targetPaths, bool overwrite, bool noPointers,
        Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>> confirmRehydration)
    {
        var mediator = provider.GetRequiredService<IMediator>();
        var targets = targetPaths.Count == 0 ? new string?[] { null } : targetPaths.Cast<string?>().ToArray();
        var totalPending = 0;

        foreach (var target in targets)
        {
            sink.Log(target is null ? "Resolving whole repository…" : $"Resolving {target}…", "meta");
            var result = await mediator.Send(new RestoreCommand(new RestoreOptions
            {
                RootDirectory      = destination,
                Version            = version,
                TargetPath         = target is null ? null : RelativePath.Parse(target),
                Overwrite          = overwrite,
                NoPointers         = noPointers,
                ConfirmRehydration = confirmRehydration,
            }), sink.Cts.Token);

            if (!result.Success) return (totalPending, false, result.ErrorMessage);
            totalPending += result.ChunksPendingRehydration;
        }
        return (totalPending, true, null);
    }
```

Then `RunRestoreAsync`'s try-body becomes: create the provider, build the prompting callback (Task 5), call `RunRestoreOnceAsync`, handle decline/timeout (Task 5), then the pending branch (next step).

- [ ] **Step 4: Branch on pending in `RunRestoreAsync`**

Replace the plain `database.CompleteJob(jobId, "completed", …)` success block with:

```csharp
            if (!success)
            {
                database.CompleteJob(jobId, "failed", 0, error);
                sink.Done("failed", error ?? "Restore failed.");
                return;
            }

            if (pending > 0)
            {
                var priority = runApprovedPriority?.ToString() ?? "Standard";
                var resume = ResumeParamsFor(lastEstimate, version, targetPaths, destination, overwrite, noPointers,
                                             priority, autoResume: true, startedAt: DateTimeOffset.UtcNow);
                sink.SetRehydrationResumeCounts(resume);   // fold live rehydration counts into resume (Step 5)
                database.SetJobStatus(jobId, "rehydrating", $"{pending} chunk(s) rehydrating");
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume)));
                sink.Log($"{pending} chunk(s) rehydrating — will auto-resume", "warn");
                // Non-terminal: no Done. The poller (Task 7) re-drives on its cadence; the finally releases the gate.
                return;
            }

            database.CompleteJob(jobId, "completed", 100, "Restore complete.");
            database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
            database.SetJobOutcome(jobId, JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, snapshotTimestamp: null)));
            sink.Done("completed", "Restore complete.");
```

where `(pending, success, error)` are the destructured result of `RunRestoreOnceAsync`.

- [ ] **Step 5: Fold live rehydration counts into the resume (JobSink helper)**

The poller's cadence tightens once a chunk has become available (Task 7). Record the count on the resume from the sink's live rehydration state. Add to `JobSink.cs`:

```csharp
    /// <summary>Copies the current available+rehydrated chunk count into <paramref name="resume"/> (used by the
    /// poller to tighten cadence once rehydration has started producing ready chunks). Returns the same instance
    /// with the count applied.</summary>
    public RestoreResumeState WithLiveRehydrationCounts(RestoreResumeState resume) =>
        resume with { AvailableOrRehydratedCount = _rehydAvailable + _rehydRehydrated };
```

Replace the `sink.SetRehydrationResumeCounts(resume);` line in Step 4 with `resume = sink.WithLiveRehydrationCounts(resume);` (fixing the method name — use `WithLiveRehydrationCounts`).

- [ ] **Step 6: Implement `ResumeRestoreAsync` + `ApproveAndResumeAsync`**

Add to `JobRunner`:

```csharp
    /// <summary>Re-drives a parked/rehydrating restore with the SAME jobId, non-prompting (honors the persisted
    /// priority — no re-charge, no connection). Loads resume params from state_json, flips the row to running for
    /// the (short) re-run, then back to rehydrating (still pending) or completed. Exempt from the single-active-job
    /// guard: it UPDATEs the existing row, never InsertJob. Design §7.</summary>
    public async Task ResumeRestoreAsync(string jobId)
    {
        var job = database.GetJob(jobId);
        if (job is null || job.StateJson is null) return;
        PersistedJobState? persisted;
        try { persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
        catch (JsonException) { return; }
        if (persisted?.Resume is null) return;
        var resume = persisted.Resume;

        var repo = database.GetRepository(job.RepositoryId);
        if (repo is null) return;

        var sink = new JobSink(jobId, hub);
        jobStates.Register(jobId, sink);
        sink.StartReporting();

        var gate = LockFor(job.RepositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            database.SetJobStatus(jobId, "running", "Resuming restore…");
            provider = await registry.CreateJobProviderAsync(job.RepositoryId, PreflightMode.ReadOnly, sink, sink.Cts.Token);

            var persistedPriority = resume.Priority == "High" ? RehydratePriority.High : RehydratePriority.Standard;
            var (pending, success, error) = await RunRestoreOnceAsync(
                provider, sink, jobId, resume.Destination, resume.Version, resume.TargetPaths,
                resume.Overwrite, resume.NoPointers,
                confirmRehydration: (_, _) => Task.FromResult<RehydratePriority?>(persistedPriority));

            if (!success)
            {
                database.CompleteJob(jobId, "failed", 0, error);
                sink.Done("failed", error ?? "Restore failed.");
                return;
            }
            if (pending > 0)
            {
                var next = resume with { LastRunAt = DateTimeOffset.UtcNow };
                next = sink.WithLiveRehydrationCounts(next);
                database.SetJobStatus(jobId, "rehydrating", $"{pending} chunk(s) rehydrating");
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, next)));
                return;
            }
            database.CompleteJob(jobId, "completed", 100, "Restore complete.");
            database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
            database.SetJobOutcome(jobId, JsonSerializer.Serialize(sink.BuildOutcome(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));
            sink.Done("completed", "Restore complete.");
        }
        catch (OperationCanceledException)
        {
            database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
            sink.Done("cancelled", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore resume {JobId} failed", jobId);
            database.CompleteJob(jobId, "failed", 0, ex.Message);
            sink.Done("failed", ex.Message);
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            sink.StopReporting();
            jobStates.Remove(jobId);
            sink.Cts.Dispose();
            gate.Release();
        }
    }

    /// <summary>Restart/late-answer cost fallback: records the chosen priority into the parked job's resume state,
    /// then re-drives it (design §8). Used when no live approval wait exists.</summary>
    public async Task ApproveAndResumeAsync(string jobId, RehydratePriority priority)
    {
        var job = database.GetJob(jobId);
        if (job?.StateJson is null) return;
        PersistedJobState? persisted;
        try { persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
        catch (JsonException) { return; }
        if (persisted?.Resume is null)
        {
            // Parked at awaiting-cost before any resume params existed: seed a minimal resume from the row is not
            // possible (targets unknown), so fall back to marking cancelled — the client will re-issue the restore.
            database.CompleteJob(jobId, "cancelled", 0, "Cost approval expired; please restart the restore.");
            return;
        }
        var updated = persisted.Resume with { Priority = priority.ToString(), AutoResume = true };
        database.SaveJobState(jobId, JsonSerializer.Serialize(persisted with { Resume = updated }));
        await ResumeRestoreAsync(jobId);
    }
```

Note: the awaiting-cost timeout path (Task 5) persists resume params (targets known), so `ApproveAndResumeAsync` finds them. The "cancelled" fallback covers only a truly param-less state (defensive).

- [ ] **Step 7: Finish the parked branches + add auto-resume hub methods**

In `JobsHub.cs`, inject `JobRunner jobRunner` is already present. Complete the parked branches:

```csharp
    public async Task ApproveRestore(string jobId, string? priority)
    {
        RehydratePriority? chosen = priority?.ToLowerInvariant() switch
        {
            "standard" => RehydratePriority.Standard,
            "high"     => RehydratePriority.High,
            _          => null,
        };
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, chosen); return; }   // in-run
        if (chosen is null) { await DeclineParkedAsync(jobId); return; }
        _ = jobRunner.ApproveAndResumeAsync(jobId, chosen.Value);                        // parked fallback
    }

    public async Task DeclineRestore(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return; }
        await DeclineParkedAsync(jobId);
    }

    private Task DeclineParkedAsync(string jobId)
    {
        database.SetJobStatus(jobId, "cancelled");                 // frees the guard; poller skips it
        database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
        return Task.CompletedTask;
    }

    /// <summary>Toggles auto-resume for a rehydrating restore. OFF stops the poller re-driving it (status stays
    /// rehydrating; the UI shows "≈ hydrated by" + a manual Restore-now). ON re-drives immediately.</summary>
    public async Task SetAutoResume(string jobId, bool autoResume)
    {
        var job = database.GetJob(jobId);
        if (job?.StateJson is null) return;
        PersistedJobState? persisted;
        try { persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
        catch (System.Text.Json.JsonException) { return; }
        if (persisted?.Resume is null) return;
        var updated = persisted with { Resume = persisted.Resume with { AutoResume = autoResume } };
        database.SaveJobState(jobId, System.Text.Json.JsonSerializer.Serialize(updated));
        if (autoResume) _ = jobRunner.ResumeRestoreAsync(jobId);
    }

    /// <summary>Manual "Restore now" for a rehydrating restore whose auto-resume is off.</summary>
    public Task ResumeRestore(string jobId)
    {
        _ = jobRunner.ResumeRestoreAsync(jobId);
        return Task.CompletedTask;
    }
```

Also complete `CancelJob` (Task 4 left it live-only):

```csharp
    public Task CancelJob(string jobId)
    {
        if (jobStates.CancelLive(jobId)) return Task.CompletedTask;   // live → cooperative cancel
        approvals.Resolve(jobId, null);                               // release any waiting approval
        database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");    // parked → terminal
        return Task.CompletedTask;
    }
```

- [ ] **Step 8: Build + run the focused test + full Api suite**

Run: `dotnet build src/Arius.Api` then `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/PersistedJobStateTests/*"` then `dotnet test --project src/Arius.Api.Tests`.
Expected: clean build; PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api.Tests/Jobs/PersistedJobStateTests.cs
git commit -m "feat(api): rehydration branch + same-jobId ResumeRestoreAsync + auto-resume/cancel controls"
```

---

## Task 7: `RehydrationPollingService` (restart-safe re-driver)

**Files:**
- Create: `src/Arius.Api/Jobs/RehydrationSchedule.cs`
- Create: `src/Arius.Api/Jobs/RehydrationPollingService.cs`
- Modify: `src/Arius.Api/Program.cs` (register the hosted service)
- Test: `src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs`

**Interfaces:**
- Produces: `static class RehydrationSchedule` with `bool IsDue(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset lastRunAt, string priority, bool firstChunkSeen)`.
- Consumes: Task 2 (`ListActiveRehydrations`), Task 3 (`PersistedJobState`/`RestoreResumeState`), Task 6 (`JobRunner.ResumeRestoreAsync`).

**Context:** Mirrors `SchedulerService`: a `BackgroundService` with a 1-minute `PeriodicTimer`, rebuilding its work list from `ListActiveRehydrations()` each tick (no per-job timers → restart-safe; on startup it naturally re-arms every `rehydrating` row). Adaptive cadence (design §7): High → every 15 min from start; Standard → nothing for the first ~10 h, then hourly; once a re-run has reported ≥1 available/rehydrated chunk, tighten to every 15 min. Auto-resume OFF → skip (the UI drives a manual Restore-now instead). The due decision is a pure function so it is unit-tested without a real clock or DB.

- [ ] **Step 1: Write the failing schedule test**

```csharp
// src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs
using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class RehydrationScheduleTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task High_priority_is_due_every_15_minutes()
    {
        RehydrationSchedule.IsDue(T0.AddMinutes(10), T0, T0, "High", firstChunkSeen: false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddMinutes(15), T0, T0, "High", firstChunkSeen: false).ShouldBeTrue();
    }

    [Test]
    public async Task Standard_priority_waits_ten_hours_then_hourly()
    {
        RehydrationSchedule.IsDue(T0.AddHours(9), T0, T0, "Standard", firstChunkSeen: false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddHours(10), T0, T0, "Standard", firstChunkSeen: false).ShouldBeTrue();
        // after a re-run at +10h, next due is +1h later
        RehydrationSchedule.IsDue(T0.AddHours(10).AddMinutes(30), T0, T0.AddHours(10), "Standard", false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddHours(11), T0, T0.AddHours(10), "Standard", false).ShouldBeTrue();
    }

    [Test]
    public async Task Once_a_chunk_is_seen_cadence_tightens_to_15_minutes_regardless_of_priority()
    {
        RehydrationSchedule.IsDue(T0.AddMinutes(15), T0, T0, "Standard", firstChunkSeen: true).ShouldBeTrue();
        RehydrationSchedule.IsDue(T0.AddMinutes(10), T0, T0, "Standard", firstChunkSeen: true).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/RehydrationScheduleTests/*"`
Expected: FAIL (`RehydrationSchedule` missing).

- [ ] **Step 3: Implement the pure cadence function**

```csharp
// src/Arius.Api/Jobs/RehydrationSchedule.cs
namespace Arius.Api.Jobs;

/// <summary>Adaptive rehydration re-drive cadence (design §7). Pure, clock-injected so it is unit-testable.
/// High: every 15 min from start. Standard: quiet for the first ~10 h (rehydration can't finish sooner), then
/// hourly. Once a re-run has seen ≥1 chunk become available, tighten to every 15 min regardless of priority.</summary>
public static class RehydrationSchedule
{
    private static readonly TimeSpan Tight    = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Standard1 = TimeSpan.FromHours(10);
    private static readonly TimeSpan StandardN = TimeSpan.FromHours(1);

    public static bool IsDue(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset lastRunAt, string priority, bool firstChunkSeen)
    {
        var sinceLast = now - lastRunAt;
        if (firstChunkSeen || priority == "High")
            return sinceLast >= Tight;

        // Standard, no chunk seen yet: one quiet window from start, then hourly re-checks.
        var elapsed = now - startedAt;
        return elapsed < Standard1 ? sinceLast >= Standard1 : sinceLast >= StandardN;
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/RehydrationScheduleTests/*"`
Expected: PASS.

- [ ] **Step 5: Implement the polling service**

```csharp
// src/Arius.Api/Jobs/RehydrationPollingService.cs
using System.Text.Json;

namespace Arius.Api.Jobs;

/// <summary>
/// Re-drives pending (rehydrating) restores until they complete. Wakes every minute, rebuilds its work list from
/// <see cref="AppData.AppDatabase.ListActiveRehydrations"/> (no per-job timers → survives restart; re-arms every
/// rehydrating row on startup), and for each auto-resume job whose adaptive cadence is due, calls
/// <see cref="JobRunner.ResumeRestoreAsync"/>. Auto-resume=off jobs are skipped (a manual "Restore now" drives them).
/// Mirrors <see cref="SchedulerService"/>. Design §7.
/// </summary>
public sealed class RehydrationPollingService(IServiceProvider services, ILogger<RehydrationPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { Tick(); }
            catch (Exception ex) { logger.LogError(ex, "Rehydration poll tick failed"); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private void Tick()
    {
        var database = services.GetRequiredService<AppData.AppDatabase>();
        var runner   = services.GetRequiredService<JobRunner>();
        var now      = DateTimeOffset.UtcNow;

        foreach (var job in database.ListActiveRehydrations())
        {
            if (job.StateJson is null) continue;
            PersistedJobState? persisted;
            try { persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
            catch (JsonException) { continue; }
            var resume = persisted?.Resume;
            if (resume is null || !resume.AutoResume) continue;

            var firstChunkSeen = resume.AvailableOrRehydratedCount > 0;
            if (!RehydrationSchedule.IsDue(now, resume.RehydrationStartedAt, resume.LastRunAt, resume.Priority, firstChunkSeen))
                continue;

            logger.LogInformation("Re-driving rehydrating restore {JobId} (repo {RepositoryId})", job.Id, job.RepositoryId);
            _ = runner.ResumeRestoreAsync(job.Id);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 6: Register the hosted service**

In `Program.cs`, after `builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();` (line ~42):

```csharp
    builder.Services.AddHostedService<Arius.Api.Jobs.RehydrationPollingService>();
```

- [ ] **Step 7: Build + commit**

Run: `dotnet build src/Arius.Api` (expect clean) then `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/RehydrationScheduleTests/*"`.

```bash
git add src/Arius.Api/Jobs/RehydrationSchedule.cs src/Arius.Api/Jobs/RehydrationPollingService.cs src/Arius.Api/Program.cs src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs
git commit -m "feat(api): restart-safe rehydration polling service with adaptive cadence"
```

---

## Task 8: Attach protocol + jobId-tagged Done/outcome + final-emit ordering

**Files:**
- Modify: `src/Arius.Api/Contracts/JobDetailDtos.cs` (add `JobAttachState`)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`AttachToJob`/`DetachFromJob`; `Done` carries `jobId`+`outcome`)
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (`Done(status, summary, outcomeJson?)`; stop the timer's post-`Done` progress race)
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (pass outcome into `Done` on completion; call `StopReporting` before `Done`)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs` (extend with a no-emit-after-stop assertion) — or a focused new test.

**Interfaces:**
- Produces: `JobAttachState(string Status, JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount)`.
- Produces on `JobsHub`: `Task<JobAttachState?> AttachToJob(string jobId)`; `Task DetachFromJob(string jobId)`.
- Produces on `JobSink`: `Done(string status, string summary, string? outcomeJson = null)` (adds `jobId` + `outcome` to the message additively); `StopReporting()` no longer emits a Progress after the terminal `Done`.
- Consumes: Task 2 (`GetJob`), Task 3 (`PersistedJobState`, `WarningCount`), Task 5 (`CostEstimateDto`), Plan 1 (`JobStateRegistry.TryGet`, `BuildSnapshot`).

**Context:** Snapshot-on-attach (§5): `AttachToJob` joins the SignalR group and returns the current state in one round trip — from the live `JobSink` if the job is running, else parsed from `state_json` for a parked/finished job — so there is no gap and one client apply-path. Because messages fan out to a group and a client may attach to several jobs at once, `Done` must carry `jobId` (Progress already does; `CostEstimate` got it in Task 5). The Plan-1 timer calls `EmitNow()` inside `StopReporting()`, which currently fires a Progress **after** the terminal `Done` — a reattaching client could see a live-looking Progress after a job ended. Fix: `StopReporting` stops the timer and does a final emit **only if the job has not been marked done**; `JobRunner` emits its final snapshot then sends `Done`.

- [ ] **Step 1: Write the failing no-emit-after-done test**

```csharp
// src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs  (append)
[Test]
public async Task StopReporting_does_not_emit_progress_after_done()
{
    // An inert sink can't observe SignalR sends, so assert the guard flag instead: once Done is recorded,
    // EmitNow is suppressed. We expose this via a test-only check on the sink's terminal flag.
    var s = new JobSink();
    s.Done("completed", "done");
    s.IsDone.ShouldBeTrue();          // Done sets the terminal flag
    s.StopReporting();                // must be a no-op emit-wise (no throw; timer never started)
    s.IsDone.ShouldBeTrue();
}
```

- [ ] **Step 2: Run — expect FAIL** (`IsDone` missing)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkWarningsTests/*"`
Expected: FAIL.

- [ ] **Step 3: Add the terminal flag + outcome to `JobSink.Done`; guard `EmitNow`**

In `JobSink.cs`, replace `Done` and adjust the reporting methods:

```csharp
    private volatile bool _done;
    /// <summary>Whether a terminal <see cref="Done"/> has been sent — suppresses any late progress emit.</summary>
    public bool IsDone => _done;

    public void Done(string status, string summary, string? outcomeJson = null)
    {
        _done = true;
        Group?.SendAsync("Done", new { jobId = JobId, status, summary, outcome = outcomeJson });
    }
```

Change `EmitNow` to respect the flag:

```csharp
    public void EmitNow() { if (!_done) Group?.SendAsync("Progress", BuildSnapshot(_now())); }
```

(The timer callback and `StopReporting` are unchanged in shape; `StopReporting`'s `EmitNow()` now no-ops once `Done` has been sent.)

- [ ] **Step 4: Run the sink test — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkWarningsTests/*"`
Expected: PASS.

- [ ] **Step 5: Order final-emit before `Done` and pass the outcome (JobRunner)**

In `RunArchiveAsync`'s success branch, change the `sink.Done("completed", summary);` line so the outcome rides the terminal message, and emit the final snapshot first:

```csharp
                var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, result.SnapshotTime.ToString("O")));
                database.SetJobOutcome(jobId, outcomeJson);
                sink.EmitNow();                       // final absolute progress (100%) before the terminal message
                sink.Done("completed", summary, outcomeJson);
```

(Remove the now-duplicated `database.SetJobOutcome(...)` line above it.) Do the equivalent in `RunRestoreAsync`'s completed branch and in `ResumeRestoreAsync`'s completed branch (build the outcome json, `SetJobOutcome`, `EmitNow()`, then `Done("completed", "Restore complete.", outcomeJson)`).

- [ ] **Step 6: Add `JobAttachState` + attach/detach to the hub**

In `JobDetailDtos.cs`:

```csharp
/// <summary>Snapshot-on-attach payload (design §5): the job's current status, its absolute progress snapshot,
/// the cost modal if it is awaiting-cost, and the live warning count. One round trip, one client apply-path.</summary>
public sealed record JobAttachState(string Status, Arius.Api.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount);
```

In `JobsHub.cs` (it now injects `JobStateRegistry jobStates` from Task 4):

```csharp
    /// <summary>Joins the job's SignalR group and returns its current state — live from the registry if the job is
    /// running, else reconstructed from persisted state_json for a parked/finished job. Progress deltas follow.</summary>
    public async Task<JobAttachState?> AttachToJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);

        var job = database.GetJob(jobId);
        if (job is null) return null;

        if (jobStates.TryGet(jobId, out var sink))
            return new JobAttachState(job.Status, sink.BuildSnapshot(DateTimeOffset.UtcNow), Cost: null, sink.WarningCount);

        if (job.StateJson is not null)
        {
            try
            {
                var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                if (persisted is not null)
                    return new JobAttachState(job.Status, persisted.Snapshot, Cost: null, persisted.Warnings.Count);
            }
            catch (System.Text.Json.JsonException) { /* fall through to a bare snapshot */ }
        }
        return new JobAttachState(job.Status, EmptySnapshot(jobId), Cost: null, WarningCount: 0);
    }

    /// <summary>Leaves the job's SignalR group (the client stopped watching it).</summary>
    public Task DetachFromJob(string jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);

    private static JobSnapshot EmptySnapshot(string jobId) => new()
    {
        JobId = jobId, Phase = "unknown",
        TotalBytes = 0, TotalNewBytes = 0, ScannedBytes = 0, HashedBytes = 0, UploadedBytes = 0,
        DedupedBytes = 0, DedupedFiles = 0, EtaSeconds = null, ThroughputBytesPerSec = 0, Pct = 0,
        Stats = new Dictionary<string, string>(), WarningCount = 0,
        RestoreTotalFiles = 0, FilesRestored = 0, RestoreTotalBytes = 0, BytesRestored = 0,
        ChunksAvailable = 0, ChunksRehydrated = 0, ChunksNeedingRehydration = 0, ChunksPending = 0,
    };
```

(Add `using Arius.Api.Jobs;` and `using Arius.Api.Contracts;` to `JobsHub.cs` if not present.)

> **Cost on attach:** persisting/replaying the exact cost estimate to a reconnecting `awaiting-cost` client is deferred to Plan 3's client work — `Cost` is returned as `null` here and the client re-requests via the detail page. The status (`awaiting-cost`) is conveyed, which is enough to render the "Review cost ›" affordance. (Recorded in the ledger as a Plan-3 follow-up.)

- [ ] **Step 7: Build + run the full Api suite**

Run: `dotnet build src/Arius.Api` then `dotnet test --project src/Arius.Api.Tests`.
Expected: clean build; all PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/Contracts/JobDetailDtos.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Tests/Jobs/JobSinkWarningsTests.cs
git commit -m "feat(api): snapshot-on-attach protocol + jobId/outcome on Done + no post-Done progress"
```

---

## Task 9: REST — single-job read, warnings, and list filters

**Files:**
- Modify: `src/Arius.Api/Contracts/JobDetailDtos.cs` (add `JobDetailDto`, `JobWarningsDto`)
- Modify: `src/Arius.Api/Endpoints/JobEndpoints.cs`
- Test: `src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs` (extend with an endpoint-shape check via the DB) — endpoint wiring itself is verified by build + a focused DTO-mapping test.

**Interfaces:**
- Produces: `JobDetailDto(string Id, long RepoId, string Repo, string Kind, string Trigger, string Status, double Pct, string? Detail, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Outcome, JobSnapshot? Snapshot, int WarningCount)`; `JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated)`.
- Endpoints: `GET /jobs?repositoryId={long?}&status={active|terminal|<exact>}`, `GET /jobs/{id}`, `GET /jobs/{id}/warnings`.
- Consumes: Task 2 (`GetJob`), Task 3 (`PersistedJobState`), Task 5/8 (DTOs), `JobStateRegistry` (live warnings).

**Context:** REST serves discovery and non-live detail (§5): finished/history detail and the overview's initial load. `GET /jobs` gains a `repositoryId` filter (the pill's discovery query) and a `status` filter (`active` = the non-terminal set; `terminal` = the rest; or an exact status). `GET /jobs/{id}` returns the row plus the parsed snapshot (live from the registry if running, else from `state_json`). `GET /jobs/{id}/warnings` returns the verbatim lines (live from the sink, else from `state_json`), with a `truncated` flag when the ring cap (200) was exceeded.

- [ ] **Step 1: Add the DTOs**

In `JobDetailDtos.cs`:

```csharp
/// <summary>Full single-job payload for GET /jobs/{id}: the history row plus the parsed progress snapshot
/// (live if running, else from state_json) and the warning count.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Jobs.JobSnapshot? Snapshot, int WarningCount);

/// <summary>Verbatim per-job warnings for GET /jobs/{id}/warnings. <see cref="Truncated"/> is true when more than
/// the retained tail (200) were emitted, so <see cref="Count"/> &gt; <see cref="Lines"/>.Count.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
```

- [ ] **Step 2: Write a focused mapping test**

```csharp
// src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs  (append)
[Test]
public async Task GetJob_state_json_deserializes_to_persisted_state()
{
    var (db, repoId) = NewDatabase();
    db.InsertJob("j1", repoId, "restore", "one-off", "running");
    var sink = new Arius.Api.Jobs.JobSink();
    sink.SetRestoreTotals(2, 2000);
    db.SaveJobState("j1", System.Text.Json.JsonSerializer.Serialize(
        sink.BuildPersistedState(DateTimeOffset.UnixEpoch, resume: null)));

    var job = db.GetJob("j1")!;
    var persisted = System.Text.Json.JsonSerializer.Deserialize<Arius.Api.Jobs.PersistedJobState>(job.StateJson!)!;
    persisted.Snapshot.RestoreTotalFiles.ShouldBe(2L);
    persisted.Warnings.ShouldNotBeNull();
}
```

- [ ] **Step 3: Run — expect PASS** (verifies the persisted shape the endpoints rely on)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobLifecycleDbTests/*"`
Expected: PASS.

- [ ] **Step 4: Implement the endpoints**

Rewrite `JobEndpoints.MapJobEndpoints` (add the imports `using System.Text.Json;`, `using Arius.Api.Jobs;`):

```csharp
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/jobs", (AppDatabase db, long? repositoryId, string? status) =>
        {
            var aliases = db.ListRepositories().ToDictionary(r => r.Id, r => r.Alias);
            var nonTerminal = new HashSet<string> { "running", "awaiting-cost", "rehydrating" };
            return db.ListJobs()
                .Where(j => repositoryId is null || j.RepositoryId == repositoryId)
                .Where(j => status switch
                {
                    null or ""  => true,
                    "active"    => nonTerminal.Contains(j.Status),
                    "terminal"  => !nonTerminal.Contains(j.Status),
                    var s       => j.Status == s,
                })
                .Select(j => new JobDto(
                    j.Id, j.RepositoryId, aliases.GetValueOrDefault(j.RepositoryId, "—"),
                    j.Kind, j.Trigger, j.Status, j.Pct, j.Detail, j.StartedAt, j.FinishedAt, j.Outcome))
                .ToList();
        });

        app.MapGet("/jobs/{id}", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();
            var repo = db.ListRepositories().FirstOrDefault(r => r.Id == job.RepositoryId);

            JobSnapshot? snapshot = null;
            var warningCount = 0;
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
                    warningCount = persisted?.Warnings.Count ?? 0;
                }
                catch (JsonException) { /* leave snapshot null */ }
            }

            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome, snapshot, warningCount));
        });

        app.MapGet("/jobs/{id}/warnings", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();

            if (jobStates.TryGet(id, out var sink))
            {
                var lines = sink.Warnings;
                return Results.Ok(new JobWarningsDto(sink.WarningCount, lines, Truncated: sink.WarningCount > lines.Count));
            }
            if (job.StateJson is not null)
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                    var lines = persisted?.Warnings ?? [];
                    return Results.Ok(new JobWarningsDto(lines.Count, lines, Truncated: false));
                }
                catch (JsonException) { /* fall through */ }
            }
            return Results.Ok(new JobWarningsDto(0, [], false));
        });

        app.MapGet("/repos/{id:long}/schedules", (long id, AppDatabase db) =>
            db.ListSchedules(id).Select(ToDto).ToList());

        app.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null) return Results.NotFound();
            var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
            return Results.Created($"/api/repos/{id}/schedules/{scheduleId}", ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
        });

        app.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, AppDatabase db) =>
        {
            db.DeleteSchedule(scheduleId);
            return Results.NoContent();
        });
    }
```

(Keep the existing `ToDto(ScheduleRecord)` helper and `using Arius.Api.Contracts;`.)

- [ ] **Step 5: Build + run the full Api suite**

Run: `dotnet build src/Arius.Api` then `dotnet test --project src/Arius.Api.Tests`.
Expected: clean build; all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Api/Contracts/JobDetailDtos.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api.Tests/AppData/JobLifecycleDbTests.cs
git commit -m "feat(api): GET /jobs/{id} + /jobs/{id}/warnings + repositoryId/status list filters"
```

---

## Self-review checklist (completed)

**Spec coverage:**
- §5 reattach protocol → Task 8 (`AttachToJob`/`DetachFromJob` + snapshot from live/StateJson), Task 9 (REST split by liveness). Client-side reconnect re-attach is Plan 3.
- §6 guard multiplicity → already enforced by Plan 1's index; Task 6 keeps resume exempt (UPDATE, never InsertJob).
- §7 rehydration auto-resume → Task 6 (branch on `ChunksPendingRehydration`, `ResumeRestoreAsync`, non-prompting priority, same jobId) + Task 7 (poller, adaptive cadence, restart-safe) + Task 6 (`SetAutoResume`/`ResumeRestore`). OFF-mode "≈ hydrated by" value plumbed via Task 1 wait windows + persisted `RehydrationWindow`.
- §8 cost lifecycle → Task 5 (bounded 15-min wait, jobId-keyed, park on timeout, drop disconnect-decline, cost payload + wait windows) + Task 6 (`ApproveAndResumeAsync` restart/late-answer fallback).
- §9 cancellation → Task 4 (per-job CTS, token threading, OCE→cancelled) + Task 6 (`CancelJob` parked branch). Paid-rehydration confirm dialog is client-side (Plan 3); cancel keeps rehydrated copies (constraint: no `ConfirmCleanup`).
- §12 status vocab → all transitions use exactly `running`/`awaiting-cost`/`rehydrating`/`completed`/`failed`/`cancelled`; index unchanged.
- §13 history outcomes → Task 8 carries `outcome` on `Done`; Task 9 exposes `Outcome` (list) + `Snapshot` (detail).
- §14 Api surface → hub methods (Tasks 4/5/6/8), REST (Task 9), warnings capture/retention (Task 3 + Task 9). The per-job fan-out **Serilog** sink is realized as a `JobSink`-level warn/error capture (simpler, boundary-clean, captures every forwarder warning) — recorded as a deliberate deviation below.
- §18 wait windows → Task 1 (const in Azure estimator, surfaced via `RestoreCostEstimate`).

**Deliberate deviations from the spec (flag at execution handoff):**
1. **No periodic ~5–10 s `state_json` flush** (design §4 mentions it). A running job's live state is read from the registry while live and reconciled to `interrupted` on restart before any client can attach, so a periodic flush has no consumer. `state_json` is written only at transitions (`awaiting-cost`, `rehydrating`) + completion. Simpler, no flush-overwrite race with resume params.
2. **Warnings capture via `JobSink.Log(warn/error)`**, not a custom Serilog in-memory sink. Captures every operationally-meaningful line the forwarders emit; persisted in `state_json`; consultable after restart (the user's stated requirement). The full Serilog fan-out remains a possible richer follow-up.
3. **Cost estimate not replayed on attach** (`JobAttachState.Cost` = null): status conveys `awaiting-cost`; the client re-fetches the estimate on the detail page. Deferred to Plan 3.

**Placeholder scan:** no TBD/TODO; every code step shows complete code. The two "confirm the test home / verify shape" steps (Task 1 Step 1, Task 6 Step 2) point at concrete existing patterns and assert real behavior.

**Type consistency:** `PersistedJobState`/`RestoreResumeState` (Task 3) consumed unchanged in Tasks 6–9; `CostEstimateDto` (Task 5) reused in Task 8's `JobAttachState`; `ApprovalResult`/`RegisterAsync` (Task 5) match `RunRestoreAsync`'s caller; `RestoreCostEstimate.StandardWait`/`HighWait` (Task 1) consumed in Task 5's `ResumeParamsFor` and cost push; `JobSnapshot.WarningCount` (Task 3) set in `BuildSnapshot` and read in Tasks 8–9; `ResumeRestoreAsync`/`ApproveAndResumeAsync`/`RunRestoreOnceAsync` signatures consistent between Task 6 definition and Task 7/hub callers; `SetJobStatus`/`GetJob`/`ListActiveRehydrations` (Task 2) match every caller.

**Cross-task build note:** Task 5's `RunRestoreAsync` edits reference `ResumeParamsFor`/`lastEstimate`/the decline-timeout branches; Task 6 then extracts `RunRestoreOnceAsync` and adds the pending branch. The two tasks both touch `RunRestoreAsync` — the Task-5 implementer leaves the method compiling (its added branches `return` before the untouched `CompleteJob("completed")`), and Task 6 refactors it. Each task's steps end at a clean build.
