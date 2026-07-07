# Jobs awaiting-cost Lifecycle + Live-State Cluster — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the highest-severity cluster from the `jobs-progress` code review by reworking the restore cost-approval lifecycle so an unanswered cost prompt holds the *same* live run for up to 24h and is then auto-cancelled by a server-side sweep, and by fixing four related live-state defects (concurrent-resume registry clobbering, parked-cancel not reaching clients, reconnect dropping the cost estimate, and the triplicated job-view resolution).

**Architecture:** The restore run blocks in its `ConfirmRehydration` callback on an *unbounded* wait (no 15-minute in-process timeout). A new `StaleApprovalSweepService` (`BackgroundService`, hourly) declines any `awaiting-cost` job older than 24h — the single owner of "abandoned prompt" cleanup, off the request/hub path. Removing the timeout collapses the live-vs-parked `awaiting-cost` split, deleting the timeout machinery, the park-and-persist branch, and the now-dead parked-approve fallback. A shared `JobRunner.CancelParked` helper broadcasts a terminal `Done` for genuinely parked (rehydrating) jobs, and a shared `JobViewResolver` replaces the copy-pasted "live sink → persisted state_json → empty" resolution across the hub and the two REST endpoints.

**Tech Stack:** .NET 10, C#, ASP.NET Core Minimal APIs + SignalR, Mediator (source-gen), Microsoft.Data.Sqlite; Angular 20 (signals) + `@microsoft/signalr`; TUnit (`Microsoft.Testing.Platform`) for API integration tests; Vitest for web unit tests.

## Global Constraints

- Target framework: **net10.0**; C# `sealed` types, primary constructors, file-scoped namespaces — match the surrounding style in each file.
- Non-terminal statuses are exactly `running | awaiting-cost | rehydrating` (`src/Arius.Api/AppData/JobStatuses.cs`). **Do not add, rename, or reorder statuses**, and **do not touch the `ux_jobs_one_active_per_repo` unique index or the DB schema** — the repo-blocking behaviour of `awaiting-cost` is intended.
- The `Done` SignalR message payload shape is **exactly** `new { jobId, status, summary, outcome }` (see `JobSink.Done`, `src/Arius.Api/Jobs/JobSink.cs:74`). Any new terminal broadcast MUST reuse this shape — the Angular `DoneMsg` client contract depends on it.
- API integration tests: TUnit `[Test]` methods, `await Assert.That(x).Is…()`, driven through `AriusApiFactory` (`src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs`). Run with `dotnet test --project src/Arius.Api.Integration.Tests`.
- Web unit tests: Vitest, `environment: 'node'`. Run with `cd src/Arius.Web && npm test` (single file: `npx vitest run <path>`).
- Keep every commit green: `dotnet build src/Arius.slnx` must compile after each task.

---

## File Structure

**Modified (API):**
- `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs` — drop the timeout; `RegisterAsync` becomes an unbounded, cancellable wait returning `RehydratePriority?`.
- `src/Arius.Api/Jobs/JobRunner.cs` — simplify the restore cost callback (no `costTimedOut`/park branch); add `CancelParked`; move `ResumeRestoreAsync` registration under the repo gate; delete `ApproveAndResumeAsync`.
- `src/Arius.Api/Hubs/JobsHub.cs` — parked cancel/decline broadcast `Done` via `CancelParked`; simplify `ApproveRestore` (remove parked fallback); use the shared resolver in `AttachToJob`.
- `src/Arius.Api/Endpoints/JobEndpoints.cs` — use the shared resolver in `GET /jobs/{id}` and `GET /jobs/{id}/warnings`.
- `src/Arius.Api/AppData/AppDatabase.cs` — add `ListStaleAwaitingCost(DateTimeOffset)`.
- `src/Arius.Api/AriusApiHost.cs` — register `StaleApprovalSweepService`.
- `src/Arius.Api/Jobs/SchedulerService.cs`, `src/Arius.Api/Jobs/RehydrationPollingService.cs` — adopt the shared periodic-timer helper (removes duplicated `SafeWaitAsync`).

**Created (API):**
- `src/Arius.Api/Jobs/StaleApprovalSweepService.cs` — the 24h abandoned-approval sweep.
- `src/Arius.Api/Jobs/PeriodicTimerExtensions.cs` — shared cancellation-safe `WaitForNextTickAsync`.
- `src/Arius.Api/Jobs/JobViewResolver.cs` — `ResolvedJobView` + the shared live/persisted resolver.

**Modified (Web):**
- `src/Arius.Web/src/app/core/api/realtime.service.ts` — extract `forwardReattach`; forward the cost estimate on reconnect.

**Created (Web):**
- `src/Arius.Web/src/app/core/api/realtime.service.spec.ts` — unit tests for `forwardReattach`.

**Tests (API):**
- `src/Arius.Api.Integration.Tests/StaleApprovalSweepTests.cs` (new)
- `src/Arius.Api.Integration.Tests/ParkedCancelBroadcastTests.cs` (new)
- `src/Arius.Api.Integration.Tests/ConcurrentResumeTests.cs` (new)
- `src/Arius.Api.Integration.Tests/JobViewResolverTests.cs` (new)

---

## Task 1: Remove the 15-minute approval timeout (unbounded, cancellable wait)

**Files:**
- Modify: `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs`
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (`RunRestoreAsync` cost callback + post-run branches)

**Interfaces:**
- Produces: `Task<RehydratePriority?> RestoreApprovalRegistry.RegisterAsync(string jobId, CancellationToken ct)` — resolves to the approved priority, or `null` when declined; throws `OperationCanceledException` if `ct` fires (process shutdown). `Resolve(string, RehydratePriority?)` and `HasPending(string)` are unchanged. The `ApprovalResult` record and the `TimeSpan timeout` parameter are removed.
- Consumes (later tasks): the fact that an `awaiting-cost` job is now always *live* within a process lifetime (its run is blocked in the callback, so its `JobSink` stays registered in `JobStateRegistry`).

- [ ] **Step 1: Write the failing test**

Add to a new file `src/Arius.Api.Integration.Tests/ApprovalRegistryTests.cs`:

```csharp
using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Integration.Tests;

public class ApprovalRegistryTests
{
    [Test]
    public async Task RegisterAsync_returns_the_resolved_priority()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("job-1", CancellationToken.None);
        await Assert.That(reg.HasPending("job-1")).IsTrue();

        reg.Resolve("job-1", RehydratePriority.High);

        await Assert.That(await wait).IsEqualTo(RehydratePriority.High);
        await Assert.That(reg.HasPending("job-1")).IsFalse();   // entry removed after resolve
    }

    [Test]
    public async Task RegisterAsync_returns_null_when_declined()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("job-2", CancellationToken.None);
        reg.Resolve("job-2", null);
        await Assert.That(await wait).IsNull();
    }

    [Test]
    public async Task RegisterAsync_throws_when_the_token_is_cancelled()
    {
        var reg = new RestoreApprovalRegistry();
        using var cts = new CancellationTokenSource();
        var wait = reg.RegisterAsync("job-3", cts.Token);
        cts.Cancel();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ApprovalRegistryTests"`
Expected: FAIL to compile — `RegisterAsync` still takes a `TimeSpan` and returns `Task<ApprovalResult>`.

- [ ] **Step 3: Simplify `RestoreApprovalRegistry`**

Replace the `ApprovalResult` record and the `RegisterAsync` method in `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs` with:

```csharp
/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback until the client answers the cost modal
/// (<c>JobsHub.ApproveRestore</c>/<c>DeclineRestore</c>) or the run's token is cancelled. Keyed by jobId, so ANY
/// connection may answer. There is no in-process timeout: an unanswered prompt keeps the run live (holding its
/// read provider) until answered, until the run's token is cancelled (process shutdown), or until the
/// out-of-band <see cref="StaleApprovalSweepService"/> declines it after 24h. <c>null</c> = decline.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Awaited by the restore command. Completes with the approved priority, or <c>null</c> on decline.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled. Always removes its
    /// own pending entry.</summary>
    public async Task<RehydratePriority?> RegisterAsync(string jobId, CancellationToken ct)
    {
        var tcs = _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(jobId, out _);
        }
    }

    /// <summary>Completes the pending approval for a job (priority to proceed, or <c>null</c> to decline). No-op
    /// if nothing is waiting.</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryGetValue(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    /// <summary>Whether a live wait is currently parked for this job.</summary>
    public bool HasPending(string jobId) => _pending.ContainsKey(jobId);
}
```

Delete the now-unused `public sealed record ApprovalResult(...)` line near the top of the file.

- [ ] **Step 4: Simplify the `RunRestoreAsync` cost callback and post-run branches**

In `src/Arius.Api/Jobs/JobRunner.cs`, inside `RunRestoreAsync`:

Replace the local flags `var costDeclined = false;` and `var costTimedOut = false;` with a single `var costDeclined = false;` (drop `costTimedOut`).

Replace the tail of the `confirmRehydration` callback (from `database.SetJobStatus(jobId, "awaiting-cost", …)` onward) with:

```csharp
                    database.SetJobStatus(jobId, "awaiting-cost", "Awaiting cost approval");

                    var priority = await approvals.RegisterAsync(jobId, ct);
                    if (priority is not null)
                    {
                        sink.ClearPending();   // leaving the prompt — a later reattach is mid-restore, not awaiting one
                        runApprovedPriority = priority;
                        database.SetJobStatus(jobId, "running");
                        return priority;
                    }

                    costDeclined = true;
                    sink.Log("Restore declined.", "warn");
                    return null;   // Core exits with ChunksPendingRehydration = the still-needed count
                },
                shouldStop: () => costDeclined);
```

Then delete the entire `if (costTimedOut) { … }` block (the one that persisted resume + cost and `return`ed without a `Done`). Leave the `if (costDeclined)`, `if (pending > 0)`, and the completed-restore branches unchanged.

- [ ] **Step 5: Build, then run the tests to verify they pass**

Run: `dotnet build src/Arius.slnx`
Expected: builds with no errors (no remaining references to `ApprovalResult` or the 3-arg `RegisterAsync`).

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ApprovalRegistryTests"`
Expected: PASS (3 tests).

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~RestoreCostHandshakeTests|FullyQualifiedName~ReattachScenarioTests|FullyQualifiedName~SingleActiveJobScenarioTests"`
Expected: PASS — these already drive approval via `approvals.Resolve(...)`, so they exercise the unbounded wait and must stay green.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Api/Jobs/RestoreApprovalRegistry.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Integration.Tests/ApprovalRegistryTests.cs
git commit -m "refactor(api): drop the 15-min cost-approval timeout — hold the live run until answered/cancelled"
```

---

## Task 2: `JobRunner.CancelParked` — broadcast a terminal Done for parked jobs (fixes #5)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (add `CancelParked`)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`CancelJob` fall-through + `DeclineParkedAsync`)
- Test: `src/Arius.Api.Integration.Tests/ParkedCancelBroadcastTests.cs`

**Interfaces:**
- Produces: `void JobRunner.CancelParked(string jobId, string summary = "Cancelled.")` — marks the job `cancelled` in the DB **and** broadcasts a terminal `Done` (`status = "cancelled"`) to the job's SignalR group. Safe to call for a job with no live sink (that is its purpose).
- Consumes: `IHubContext<JobsHub> hub` (already a `JobRunner` ctor dependency), `JobSink.Done` for the wire-format-correct broadcast.

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Api.Integration.Tests/ParkedCancelBroadcastTests.cs`. It drives a rehydrating (genuinely parked) job to a cancel through the hub-equivalent path and asserts a `Done` reached the group. Because asserting a SignalR group broadcast in-process is awkward, assert the observable effect instead: after `CancelParked`, the DB row is `cancelled` and a fresh `AttachToJob` reports the terminal status (the client's `jobDone` fires off that terminal broadcast in the browser; here we prove the server marks it terminal atomically with the intent to broadcast).

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ParkedCancelBroadcastTests
{
    [Test]
    public async Task CancelParked_marks_the_job_cancelled()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var runner = factory.Services.GetRequiredService<JobRunner>();

        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");

        runner.CancelParked(jobId);

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ParkedCancelBroadcastTests"`
Expected: FAIL to compile — `JobRunner.CancelParked` does not exist.

- [ ] **Step 3: Add `CancelParked` to `JobRunner`**

In `src/Arius.Api/Jobs/JobRunner.cs`, add this method (place it next to `ApproveAndResumeAsync`, which Task 4 removes):

```csharp
/// <summary>Cancels a job that has no live run to observe a token-cancel (a rehydrating job between poller ticks,
/// or an awaiting-cost row swept as abandoned). Marks it <c>cancelled</c> in the DB AND broadcasts the terminal
/// <c>Done</c> to its SignalR group so attached clients finalize immediately — the parked paths previously wrote
/// the DB but never told the client (review #5). A fresh <see cref="JobSink"/> reuses the exact Done wire shape.</summary>
public void CancelParked(string jobId, string summary = "Cancelled.")
{
    database.CompleteJob(jobId, "cancelled", 0, summary);
    new JobSink(jobId, hub).Done("cancelled", summary);
}
```

- [ ] **Step 4: Route the parked hub paths through `CancelParked`**

In `src/Arius.Api/Hubs/JobsHub.cs`:

Change the `CancelJob` fall-through (the last two statements before `return`) from:

```csharp
        approvals.Resolve(jobId, null);                               // parked/not-live safety no-op
        database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
        return Task.CompletedTask;
```

to:

```csharp
        approvals.Resolve(jobId, null);                               // parked/not-live safety no-op
        jobRunner.CancelParked(jobId);                               // mark cancelled + broadcast Done (review #5)
        return Task.CompletedTask;
```

Change `DeclineParkedAsync` from:

```csharp
    private Task DeclineParkedAsync(string jobId)
    {
        database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
        return Task.CompletedTask;
    }
```

to:

```csharp
    private Task DeclineParkedAsync(string jobId)
    {
        jobRunner.CancelParked(jobId);   // mark cancelled + broadcast Done (review #5)
        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Build, then run the test to verify it passes**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean.

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ParkedCancelBroadcastTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api.Integration.Tests/ParkedCancelBroadcastTests.cs
git commit -m "fix(api): broadcast terminal Done when cancelling a parked job (review #5)"
```

---

## Task 3: Shared periodic-timer helper + `StaleApprovalSweepService` (fixes #1)

**Files:**
- Create: `src/Arius.Api/Jobs/PeriodicTimerExtensions.cs`
- Create: `src/Arius.Api/Jobs/StaleApprovalSweepService.cs`
- Modify: `src/Arius.Api/AppData/AppDatabase.cs` (add `ListStaleAwaitingCost`)
- Modify: `src/Arius.Api/AriusApiHost.cs` (register the service)
- Modify: `src/Arius.Api/Jobs/SchedulerService.cs`, `src/Arius.Api/Jobs/RehydrationPollingService.cs` (adopt the shared helper; remove their duplicated `SafeWaitAsync`)
- Test: `src/Arius.Api.Integration.Tests/StaleApprovalSweepTests.cs`

**Interfaces:**
- Produces:
  - `static Task<bool> PeriodicTimerExtensions.SafeWaitForNextTickAsync(this PeriodicTimer timer, CancellationToken token)` — returns `false` instead of throwing on cancellation.
  - `IReadOnlyList<JobRecord> AppDatabase.ListStaleAwaitingCost(DateTimeOffset olderThan)` — rows with `status = 'awaiting-cost'` and `started_at < olderThan`.
  - `void StaleApprovalSweepService.Sweep(DateTimeOffset cutoff)` — **`public`** (a testable seam; the `Arius.Api.Integration.Tests` project has no `InternalsVisibleTo` to `Arius.Api`, so it must be public to be called directly from the test), cancels every `awaiting-cost` row older than `cutoff`. Live rows (a pending approval) are declined via `RestoreApprovalRegistry.Resolve(jobId, null)`; any row without a live wait is cancelled via `JobRunner.CancelParked`.

> **Execution dependency:** this task's test uses `ScenarioWait.Until` from the ScenarioGate plan (`2026-07-06-scenariogate-latch.md`, Task 2). Execute that plan first so the shared helper exists — do not add a local `WaitUntil` copy here (that is the duplication that plan removes).
- Consumes: `RestoreApprovalRegistry`, `JobRunner`, `AppDatabase` (resolved per-tick from the root `IServiceProvider`, mirroring `RehydrationPollingService`).

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Api.Integration.Tests/StaleApprovalSweepTests.cs`. It parks a real restore at `awaiting-cost`, then sweeps with a cutoff in the future (so the just-started row counts as stale without waiting 24h), and asserts the repo is freed and the job cancelled.

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Api.Testing;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Api.Integration.Tests;

public class StaleApprovalSweepTests
{
    [Test]
    public async Task Sweep_cancels_an_abandoned_awaiting_cost_job_and_frees_the_repo()
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
        var jobId = Guid.NewGuid().ToString();

        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        var sweep = new StaleApprovalSweepService(factory.Services, factory.Services.GetRequiredService<ILogger<StaleApprovalSweepService>>());
        sweep.Sweep(cutoff: DateTimeOffset.UtcNow.AddMinutes(1));   // treat the just-started job as stale

        await ScenarioWait.Until(() => !db.HasActiveJob(repoId), TimeSpan.FromSeconds(10));
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
    }
}
```

`ScenarioWait.Until` is the shared polling helper created in the ScenarioGate plan (Task 2) — no local copy here.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~StaleApprovalSweepTests"`
Expected: FAIL to compile — `StaleApprovalSweepService` and `ListStaleAwaitingCost` do not exist.

- [ ] **Step 3: Add the shared periodic-timer helper**

Create `src/Arius.Api/Jobs/PeriodicTimerExtensions.cs`:

```csharp
namespace Arius.Api.Jobs;

/// <summary>Shared background-loop helper: await the next tick, returning <c>false</c> (rather than throwing)
/// when the host is stopping. Used by every <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> loop.</summary>
public static class PeriodicTimerExtensions
{
    public static async Task<bool> SafeWaitForNextTickAsync(this PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 4: Add `ListStaleAwaitingCost` to `AppDatabase`**

In `src/Arius.Api/AppData/AppDatabase.cs`, add next to `ListActiveRehydrations` (~line 433):

```csharp
/// <summary>Jobs stuck at <c>awaiting-cost</c> whose <c>started_at</c> predates <paramref name="olderThan"/> —
/// the abandoned-cost-prompt work list for <see cref="Arius.Api.Jobs.StaleApprovalSweepService"/>. Never returns
/// <c>rehydrating</c> rows (those legitimately live for hours).</summary>
public IReadOnlyList<JobRecord> ListStaleAwaitingCost(DateTimeOffset olderThan)
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE status = 'awaiting-cost' AND started_at < $t;";
    command.Parameters.AddWithValue("$t", olderThan.ToString("O"));
    using var reader = command.ExecuteReader();
    var result = new List<JobRecord>();
    while (reader.Read())
        result.Add(ReadJob(reader));
    return result;
}
```

> Note on the `$t` binding: `started_at` is written by `InsertJob` as `DateTimeOffset.UtcNow`. Confirm the existing insert uses round-trip (`"O"`) ISO-8601 formatting so this string comparison orders correctly — grep `InsertJob` in `AppDatabase.cs` and match its exact `started_at` format. If it stores a different format, format `$t` identically.

- [ ] **Step 5: Add `StaleApprovalSweepService`**

Create `src/Arius.Api/Jobs/StaleApprovalSweepService.cs`:

```csharp
using Arius.Api.AppData;

namespace Arius.Api.Jobs;

/// <summary>
/// Auto-cancels restores abandoned at the cost prompt. Wakes hourly and declines any <c>awaiting-cost</c> job
/// older than <see cref="MaxApprovalAge"/> (24h) — the single owner of "closed the modal and walked away"
/// cleanup, deliberately off the hub/connection path (a dropped tab must not decide; design §8). A live run
/// (pending approval) is declined via <see cref="RestoreApprovalRegistry.Resolve"/> so its own decline branch
/// marks it cancelled + broadcasts Done; a row with no live wait is cancelled via <see cref="JobRunner.CancelParked"/>.
/// Mirrors <see cref="SchedulerService"/>/<see cref="RehydrationPollingService"/>.
/// </summary>
public sealed class StaleApprovalSweepService(IServiceProvider services, ILogger<StaleApprovalSweepService> logger) : BackgroundService
{
    public static readonly TimeSpan MaxApprovalAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { Sweep(DateTimeOffset.UtcNow - MaxApprovalAge); }
            catch (Exception ex) { logger.LogError(ex, "Stale-approval sweep tick failed"); }
        }
        while (await timer.SafeWaitForNextTickAsync(stoppingToken));
    }

    public void Sweep(DateTimeOffset cutoff)
    {
        var database  = services.GetRequiredService<AppDatabase>();
        var approvals = services.GetRequiredService<RestoreApprovalRegistry>();
        var runner    = services.GetRequiredService<JobRunner>();

        foreach (var job in database.ListStaleAwaitingCost(cutoff))
        {
            logger.LogInformation("Auto-cancelling abandoned awaiting-cost job {JobId} (older than {Age})", job.Id, MaxApprovalAge);
            if (approvals.HasPending(job.Id))
                approvals.Resolve(job.Id, null);                          // live run → decline branch marks cancelled + Done
            else
                runner.CancelParked(job.Id, "Cost approval abandoned.");  // no live wait → cancel + broadcast Done
        }
    }
}
```

- [ ] **Step 6: Register the hosted service**

In `src/Arius.Api/AriusApiHost.cs`, after the two existing `AddHostedService` lines (~line 37):

```csharp
        builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();
        builder.Services.AddHostedService<Arius.Api.Jobs.RehydrationPollingService>();
        builder.Services.AddHostedService<Arius.Api.Jobs.StaleApprovalSweepService>();
```

- [ ] **Step 7: Dedupe the existing loops onto the shared helper**

In `src/Arius.Api/Jobs/SchedulerService.cs` and `src/Arius.Api/Jobs/RehydrationPollingService.cs`: delete the `private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token) { … }` method from each, and change each `while (await SafeWaitAsync(timer, stoppingToken))` to `while (await timer.SafeWaitForNextTickAsync(stoppingToken))`.

- [ ] **Step 8: Build, then run the tests to verify they pass**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean; no remaining `SafeWaitAsync` definitions (`grep -rn "SafeWaitAsync" src/Arius.Api` returns nothing).

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~StaleApprovalSweepTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Arius.Api/Jobs/PeriodicTimerExtensions.cs src/Arius.Api/Jobs/StaleApprovalSweepService.cs src/Arius.Api/AppData/AppDatabase.cs src/Arius.Api/AriusApiHost.cs src/Arius.Api/Jobs/SchedulerService.cs src/Arius.Api/Jobs/RehydrationPollingService.cs src/Arius.Api.Integration.Tests/StaleApprovalSweepTests.cs
git commit -m "feat(api): auto-cancel cost prompts abandoned >24h via StaleApprovalSweepService (review #1)"
```

---

## Task 4: Delete the now-dead parked-approve fallback

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (remove `ApproveAndResumeAsync`)
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (simplify `ApproveRestore`)

**Interfaces:**
- Removes: `JobRunner.ApproveAndResumeAsync(string, RehydratePriority)`. After Task 1, an `awaiting-cost` job is always live (pending approval) within a process lifetime, and a restart reconciles it to `interrupted`, so there is no reachable "approve a parked awaiting-cost job" path.

- [ ] **Step 1: Prove the fallback is unreachable**

Run: `grep -rn "ApproveAndResumeAsync" src`
Expected: exactly two hits — the definition in `JobRunner.cs` and the single call in `JobsHub.ApproveRestore`. (If any test references it, stop and reassess — the removal assumption is wrong.)

- [ ] **Step 2: Simplify `ApproveRestore`**

In `src/Arius.Api/Hubs/JobsHub.cs`, replace the body of `ApproveRestore` with:

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
        if (chosen is null) await DeclineParkedAsync(jobId);
        // else: no live approval wait to answer (job already resumed/terminal) — nothing to do.
    }
```

- [ ] **Step 3: Delete `ApproveAndResumeAsync`**

In `src/Arius.Api/Jobs/JobRunner.cs`, delete the entire `public async Task ApproveAndResumeAsync(string jobId, RehydratePriority priority) { … }` method.

- [ ] **Step 4: Build and run the full API integration suite**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean; `grep -rn "ApproveAndResumeAsync" src` returns nothing.

Run: `dotnet test --project src/Arius.Api.Integration.Tests`
Expected: PASS (whole suite).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api/Hubs/JobsHub.cs
git commit -m "refactor(api): remove dead parked-approve fallback (unreachable after timeout removal)"
```

---

## Task 5: Register the resume sink under the repo gate (fixes #3)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (`ResumeRestoreAsync`)
- Test: `src/Arius.Api.Integration.Tests/ConcurrentResumeTests.cs`

**Interfaces:**
- Behaviour change: in `ResumeRestoreAsync`, `jobStates.Register(jobId, sink)` and `sink.StartReporting()` move to *after* the repo gate is acquired and the under-gate status re-check passes, so two concurrent resumes for the same jobId serialize on the gate and each owns the registry entry only for its own critical section. The registry never holds a sink whose run is not the current gate holder.

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Api.Integration.Tests/ConcurrentResumeTests.cs`. It drives two concurrent `ResumeRestoreAsync` calls for the same rehydrating job (using a gated scenario so the first resume is held mid-run) and asserts the registry's live sink belongs to the run currently holding the gate — i.e. the job is never left visible-but-unowned.

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Api.Testing;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ConcurrentResumeTests
{
    [Test]
    public async Task Two_concurrent_resumes_do_not_leave_the_job_registered_after_both_finish()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        // A restore that completes (no pending) so each resume runs to completion.
        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents: [ new FileRestoredEvent(RelativePath.Parse("a"), 100) ],
            CostPrompt: null,
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 1, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobStates = factory.Services.GetRequiredService<JobStateRegistry>();

        // Seed a rehydrating row with resume state so ResumeRestoreAsync proceeds.
        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");
        db.SaveJobState(jobId, System.Text.Json.JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink(jobId, null!).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = [],
            Resume = new RestoreResumeState
            {
                Version = null, TargetPaths = [], Destination = dest, Overwrite = false, NoPointers = false,
                Priority = "Standard", AutoResume = true, RehydrationStartedAt = DateTimeOffset.UtcNow,
                LastRunAt = DateTimeOffset.UtcNow, RehydrationWindow = TimeSpan.FromHours(15),
            },
        }));

        var a = runner.ResumeRestoreAsync(jobId);
        var b = runner.ResumeRestoreAsync(jobId);
        await Task.WhenAll(a, b);

        // Both runs finished: the registry must not hold a stale sink, and the job is terminal exactly once.
        await Assert.That(jobStates.TryGet(jobId, out _)).IsFalse();
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails or is flaky**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ConcurrentResumeTests"`
Expected: FAIL / flaky — under the current pre-gate `Register`, the first run's `finally` can `Remove` the second run's sink (or vice-versa), leaving `TryGet` inconsistent.

- [ ] **Step 3: Move registration under the gate**

In `src/Arius.Api/Jobs/JobRunner.cs` `ResumeRestoreAsync`, change the section that currently reads:

```csharp
        var sink = new JobSink(jobId, hub);
        jobStates.Register(jobId, sink);
        sink.StartReporting();

        var gate = LockFor(job.RepositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            // Re-check under the gate: ...
            var underGate = database.GetJob(jobId);
            if (underGate is null || underGate.Status is not ("rehydrating" or "awaiting-cost")) return;

            database.SetJobStatus(jobId, "running", "Resuming restore…");
```

to:

```csharp
        var sink = new JobSink(jobId, hub);
        var registered = false;

        var gate = LockFor(job.RepositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            // Re-check under the gate: ...
            var underGate = database.GetJob(jobId);
            if (underGate is null || underGate.Status is not ("rehydrating" or "awaiting-cost")) return;

            // Register only once we hold the gate and have decided to run: concurrent resumes serialize here, so
            // the registry never holds a sink whose run isn't the current gate holder (review #3).
            jobStates.Register(jobId, sink);
            sink.StartReporting();
            registered = true;

            database.SetJobStatus(jobId, "running", "Resuming restore…");
```

Then change the `finally` block of `ResumeRestoreAsync` from:

```csharp
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            sink.StopReporting();
            jobStates.Remove(jobId);
            sink.Cts.Dispose();
            gate.Release();
        }
```

to:

```csharp
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            if (registered) { sink.StopReporting(); jobStates.Remove(jobId); }
            sink.Cts.Dispose();
            gate.Release();
        }
```

(The `if (registered)` guard prevents a bailed-before-run resume from emitting a stray `Progress` via `StopReporting`'s `EmitNow` or removing a sibling's registry entry.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ConcurrentResumeTests"`
Expected: PASS (run it a few times to confirm the flakiness is gone).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Integration.Tests/ConcurrentResumeTests.cs
git commit -m "fix(api): register the resume sink under the repo gate to stop concurrent-resume clobbering (review #3)"
```

---

## Task 6: Shared `JobViewResolver` for live/persisted job state (fixes #15)

**Files:**
- Create: `src/Arius.Api/Jobs/JobViewResolver.cs`
- Modify: `src/Arius.Api/Hubs/JobsHub.cs` (`AttachToJob`)
- Modify: `src/Arius.Api/Endpoints/JobEndpoints.cs` (`GET /jobs/{id}`, `GET /jobs/{id}/warnings`)
- Test: `src/Arius.Api.Integration.Tests/JobViewResolverTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ResolvedJobView(JobSnapshot? Snapshot, CostEstimateDto? Cost, ResumeInfo? Resume, int WarningCount, IReadOnlyList<string> Warnings)`
  - `internal static ResolvedJobView JobViewResolver.Resolve(JobStateRegistry jobStates, string jobId, string? stateJson)` — live sink if registered, else the deserialized `state_json`, else an empty view.

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Api.Integration.Tests/JobViewResolverTests.cs`:

```csharp
using Arius.Api.Contracts;
using Arius.Api.Jobs;

namespace Arius.Api.Integration.Tests;

public class JobViewResolverTests
{
    [Test]
    public async Task Resolve_prefers_the_live_sink()
    {
        var jobStates = new JobStateRegistry();
        var sink = new JobSink("job-1", null!);
        sink.SetRestoreTotals(3, 3000);
        jobStates.Register("job-1", sink);

        var view = JobViewResolver.Resolve(jobStates, "job-1", stateJson: null);

        await Assert.That(view.Snapshot).IsNotNull();
        await Assert.That(view.Snapshot!.RestoreTotalFiles).IsEqualTo(3L);
    }

    [Test]
    public async Task Resolve_falls_back_to_persisted_state_json()
    {
        var jobStates = new JobStateRegistry();   // nothing registered
        var persisted = new PersistedJobState
        {
            Snapshot = new JobSink("job-2", null!).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = ["boom"],
            Resume = null,
            Cost = null,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(persisted);

        var view = JobViewResolver.Resolve(jobStates, "job-2", json);

        await Assert.That(view.Warnings.Count).IsEqualTo(1);
        await Assert.That(view.Snapshot).IsNotNull();
    }

    [Test]
    public async Task Resolve_returns_an_empty_view_when_nothing_is_available()
    {
        var view = JobViewResolver.Resolve(new JobStateRegistry(), "missing", stateJson: null);
        await Assert.That(view.Snapshot).IsNull();
        await Assert.That(view.Cost).IsNull();
        await Assert.That(view.WarningCount).IsEqualTo(0);
        await Assert.That(view.Warnings.Count).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~JobViewResolverTests"`
Expected: FAIL to compile — `JobViewResolver`/`ResolvedJobView` do not exist.

- [ ] **Step 3: Add the resolver**

Create `src/Arius.Api/Jobs/JobViewResolver.cs`:

```csharp
using System.Text.Json;
using Arius.Api.Contracts;

namespace Arius.Api.Jobs;

/// <summary>A job's current view resolved from the single source of truth: the live <see cref="JobSink"/> if the
/// run is executing, else the persisted <c>state_json</c>, else empty. <see cref="Snapshot"/> is null only when
/// neither source exists.</summary>
public sealed record ResolvedJobView(JobSnapshot? Snapshot, CostEstimateDto? Cost, ResumeInfo? Resume, int WarningCount, IReadOnlyList<string> Warnings);

/// <summary>The one place that resolves live-sink-vs-persisted job state, shared by <c>JobsHub.AttachToJob</c> and
/// the <c>GET /jobs/{id}</c> + <c>GET /jobs/{id}/warnings</c> endpoints — previously copy-pasted three ways
/// (review #15), which let the SignalR and REST views drift for the same job.</summary>
public static class JobViewResolver
{
    public static ResolvedJobView Resolve(JobStateRegistry jobStates, string jobId, string? stateJson)
    {
        if (jobStates.TryGet(jobId, out var sink))
            return new ResolvedJobView(sink.BuildSnapshot(DateTimeOffset.UtcNow), sink.PendingCost, ResumeInfo.From(sink.PendingResume), sink.WarningCount, sink.Warnings);

        if (stateJson is not null)
        {
            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedJobState>(stateJson);
                if (persisted is not null)
                    return new ResolvedJobView(persisted.Snapshot, persisted.Cost, ResumeInfo.From(persisted.Resume), persisted.Snapshot.WarningCount, persisted.Warnings);
            }
            catch (JsonException) { /* fall through to empty */ }
        }

        return new ResolvedJobView(Snapshot: null, Cost: null, Resume: null, WarningCount: 0, Warnings: []);
    }
}
```

- [ ] **Step 4: Use the resolver in `AttachToJob`**

In `src/Arius.Api/Hubs/JobsHub.cs`, replace the body of `AttachToJob` after `if (job is null) return null;` with:

```csharp
        var view = JobViewResolver.Resolve(jobStates, jobId, job.StateJson);
        return new JobAttachState(job.Status, view.Snapshot ?? EmptySnapshot(jobId), view.Cost, view.WarningCount, view.Resume);
```

(Keep `EmptySnapshot` — it supplies the non-null snapshot `JobAttachState` requires when there is no state at all.)

- [ ] **Step 5: Use the resolver in both endpoints**

In `src/Arius.Api/Endpoints/JobEndpoints.cs`:

`GET /jobs/{id}` — replace the snapshot/cost/resume/warnings resolution block with:

```csharp
            var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome,
                view.Snapshot, view.WarningCount, view.Cost, view.Resume));
```

`GET /jobs/{id}/warnings` — replace its resolution block with:

```csharp
            var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
            return Results.Ok(new JobWarningsDto(view.WarningCount, view.Warnings, Truncated: view.WarningCount > view.Warnings.Count));
```

- [ ] **Step 6: Build and run tests**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean.

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~JobViewResolverTests|FullyQualifiedName~ReattachScenarioTests"`
Expected: PASS — `ReattachScenarioTests` proves the live-sink branch still surfaces cost/resume through the resolver.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Api/Jobs/JobViewResolver.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api.Integration.Tests/JobViewResolverTests.cs
git commit -m "refactor(api): extract JobViewResolver for the triplicated live/persisted job view (review #15)"
```

---

## Task 7: Forward the cost estimate across a SignalR reconnect (fixes #6)

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/realtime.service.ts`
- Test (create): `src/Arius.Web/src/app/core/api/realtime.service.spec.ts`

**Interfaces:**
- Produces: `forwardReattach(id: string, state: JobAttachState | null): void` on `RealtimeService` — pushes the reattach snapshot to `progress$`, and for a non-terminal job also pushes `state.cost` to `cost$`; for a terminal job pushes to `done$`. Called from the `onreconnected` handler.

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Web/src/app/core/api/realtime.service.spec.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { RealtimeService } from './realtime.service';
import { CostEstimateMsg, JobAttachState, JobSnapshot } from './api-models';

function snapshot(jobId: string): JobSnapshot {
  return {
    jobId, phase: 'restore', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0, chunkBytesTotal: 0,
  };
}

const cost: CostEstimateMsg = {
  jobId: 'j1', chunksAvailable: 3, chunksNeedingRehydration: 2, bytesNeedingRehydration: 1200,
  downloadBytes: 3000, totalStandard: 0.71, totalHigh: 4.31, standardWaitHours: 15, highWaitHours: 1,
};

describe('RealtimeService.forwardReattach', () => {
  it('re-emits the cost estimate for a non-terminal job', () => {
    const svc = new RealtimeService();
    const costs: CostEstimateMsg[] = [];
    const snaps: JobSnapshot[] = [];
    svc.cost$.subscribe(c => costs.push(c));
    svc.progress$.subscribe(s => snaps.push(s));

    const state: JobAttachState = { status: 'awaiting-cost', snapshot: snapshot('j1'), cost, warningCount: 0, resume: null };
    (svc as unknown as { forwardReattach(id: string, s: JobAttachState | null): void }).forwardReattach('j1', state);

    expect(snaps).toHaveLength(1);
    expect(costs).toEqual([cost]);
  });

  it('emits done and not cost for a terminal job', () => {
    const svc = new RealtimeService();
    const costs: CostEstimateMsg[] = [];
    const dones: string[] = [];
    svc.cost$.subscribe(c => costs.push(c));
    svc.done$.subscribe(d => dones.push(d.status));

    const state: JobAttachState = { status: 'completed', snapshot: snapshot('j1'), cost: null, warningCount: 0, resume: null };
    (svc as unknown as { forwardReattach(id: string, s: JobAttachState | null): void }).forwardReattach('j1', state);

    expect(dones).toEqual(['completed']);
    expect(costs).toEqual([]);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/Arius.Web && npx vitest run src/app/core/api/realtime.service.spec.ts`
Expected: FAIL — `forwardReattach` is not a method on `RealtimeService`.

- [ ] **Step 3: Extract `forwardReattach` and forward cost**

In `src/Arius.Web/src/app/core/api/realtime.service.ts`, replace the `onreconnected` handler (inside `ensureStarted`) with a call to the new method:

```ts
      this.connection.onreconnected(() => {
        for (const id of this.attached) {
          this.connection!.invoke<JobAttachState | null>('AttachToJob', id)
            .then(state => this.forwardReattach(id, state))
            .catch(() => {});
        }
      });
```

Then add the method to the class (e.g. just below `attachToJob`):

```ts
  /**
   * Re-applies a job's reattach state to the streams after a reconnect gap. Refreshes absolute progress; for a
   * still-active job also re-emits its cost estimate (the one-shot CostEstimate push can be lost while
   * disconnected — review #6); for a finished job emits a terminal done so consumers finalize.
   */
  private forwardReattach(id: string, state: JobAttachState | null): void {
    if (!state) return;
    this.progress$.next(state.snapshot);
    if (isNonTerminal(state.status)) {
      if (state.cost) this.cost$.next(state.cost);
    } else {
      this.done$.next({ jobId: id, status: state.status, summary: '', outcome: null });
    }
  }
```

(`isNonTerminal` and `JobAttachState` are already imported at the top of the file.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd src/Arius.Web && npx vitest run src/app/core/api/realtime.service.spec.ts`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Web/src/app/core/api/realtime.service.ts src/Arius.Web/src/app/core/api/realtime.service.spec.ts
git commit -m "fix(web): forward the cost estimate on SignalR reconnect so the modal survives a gap (review #6)"
```

---

## Task 8: Default the cost modal to Standard priority (fixes #2)

**Files:**
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` (the `priority` signal default + `aria-pressed` on the two priority buttons)
- Modify: `src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts` (assert Standard is pre-selected)

**Interfaces:**
- Behaviour change: the cost-approval modal opens with **Standard** pre-selected (was `high`), so a user who clicks "Rehydrate & restore" without changing the option is charged the lower Standard price — restoring the prior drawer default. The two priority buttons gain `[attr.aria-pressed]` reflecting the current selection (mirrors the existing `autoresume-toggle` button), which also makes the default assertable in e2e.

> Note on testing: the web project has no Angular component-test harness (Vitest runs `environment: 'node'`, and the only unit spec covers pure functions). A component-level unit test would require standing up TestBed + a DOM — disproportionate for a default-literal flip. The regression is instead pinned by the existing hermetic Playwright spec that already renders this modal.

- [ ] **Step 1: Add the failing e2e assertion**

In `src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts`, after the existing `prio-standard`/`prio-high` visibility assertions (~line 26), add:

```ts
  // #2: the modal must default to Standard (the cheaper option), not High.
  await expect(page.getByTestId('prio-standard')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('prio-high')).toHaveAttribute('aria-pressed', 'false');
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `cd src/Arius.Web && npx playwright test -c playwright.hermetic.config.ts cost-reattach`
Expected: FAIL — the buttons have no `aria-pressed` attribute yet, and the default is `high` (so even once added, `prio-standard` would be `false`).

- [ ] **Step 3: Flip the default and add `aria-pressed`**

In `src/Arius.Web/src/app/features/jobs/job-detail.component.ts`:

Change the `priority` signal default from:

```ts
  protected readonly priority = signal<'standard' | 'high'>('high');
```

to:

```ts
  protected readonly priority = signal<'standard' | 'high'>('standard');
```

Add `[attr.aria-pressed]` to the `prio-standard` button (in the modal template):

```html
                <button data-testid="prio-standard" type="button" (click)="priority.set('standard')"
                        [attr.aria-pressed]="priority() === 'standard'"
                        style="flex:1;border-radius:10px;padding:11px;text-align:left;background:#fff;cursor:pointer"
                        [style.border]="priority() === 'standard' ? '2px solid #7c3aed' : '1px solid #e4e4e7'"
                        [style.background]="priority() === 'standard' ? '#f5f3ff' : '#fff'">
```

Add `[attr.aria-pressed]` to the `prio-high` button:

```html
                <button data-testid="prio-high" type="button" (click)="priority.set('high')"
                        [attr.aria-pressed]="priority() === 'high'"
                        style="flex:1;border-radius:10px;padding:11px;text-align:left;cursor:pointer"
                        [style.border]="priority() === 'high' ? '2px solid #7c3aed' : '1px solid #e4e4e7'"
                        [style.background]="priority() === 'high' ? '#f5f3ff' : '#fff'">
```

- [ ] **Step 4: Run the spec to verify it passes**

Run: `cd src/Arius.Web && npx playwright test -c playwright.hermetic.config.ts cost-reattach`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Web/src/app/features/jobs/job-detail.component.ts src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts
git commit -m "fix(web): default the restore cost modal to Standard priority (review #2)"
```

---

## Final verification

- [ ] **Full API suite:** `dotnet test --project src/Arius.Api.Integration.Tests` → all green.
- [ ] **Web unit tests:** `cd src/Arius.Web && npm test` → all green.
- [ ] **Hermetic e2e (the reattach + single-active-job scenarios exercise this cluster):** `cd src/Arius.Web && npm run e2e:hermetic` → all green.
- [ ] **Manual smoke (matches the reported scenario):** start a restore that needs rehydration, let the cost modal show, close the tab; confirm another archive/restore is still rejected (by design); reopen `/jobs/:id`, confirm the modal re-renders with the cost and defaults to **Standard** priority (note: the Standard-default fix is review #2, a separate one-line change — flag it if not yet applied); approve and confirm the *same* run resumes.

---

## Self-Review notes

- **Spec coverage:** #1 → Tasks 1, 3, 4; #2 → Task 8; #3 → Task 5; #5 → Task 2; #6 → Task 7; #15 → Task 6. Duplicated `SafeWaitAsync` (review #28) folded into Task 3.
- **Type consistency:** `CancelParked(string, string)`, `RegisterAsync(string, CancellationToken) → Task<RehydratePriority?>`, `ListStaleAwaitingCost(DateTimeOffset) → IReadOnlyList<JobRecord>`, `Sweep(DateTimeOffset)`, `ResolvedJobView(JobSnapshot?, CostEstimateDto?, ResumeInfo?, int, IReadOnlyList<string>)`, `forwardReattach(string, JobAttachState | null)` — used consistently across tasks.
- **Ordering:** Task 2 (`CancelParked`) precedes Task 3 (sweep uses it) and Task 4. Tasks 5, 6, 7 are independent and may be executed in any order after Task 1.
