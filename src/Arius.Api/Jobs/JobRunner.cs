using System.Collections.Concurrent;
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.Hubs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Jobs;

/// <summary>
/// Runs archive/restore commands in a fresh per-job service provider, forwarding Arius.Core events to
/// the job's SignalR group and handling the restore cost-approval handshake. Writers are serialized
/// per repository so two mutating jobs never share a repo's on-disk state.
/// </summary>
public sealed class JobRunner(
    RepositoryProviderRegistry registry,
    AppDatabase database,
    IHubContext<JobsHub> hub,
    RestoreApprovalRegistry approvals,
    JobStateRegistry jobStates,
    ILogger<JobRunner> logger)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _repoLocks = new();

    private SemaphoreSlim LockFor(long repositoryId) => _repoLocks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));

    public async Task RunArchiveAsync(long repositoryId, string jobId, string tier, bool removeLocal, bool writePointers, bool fastHash = false, string trigger = "one-off")
    {
        var sink = new JobSink(jobId, hub);
        var startedAt = DateTimeOffset.UtcNow;
        var repo = database.GetRepository(repositoryId);
        if (repo is null) { sink.Done("failed", "Repository not found."); return; }
        // Race-proof backstop for the cooperative HasActiveJob check (JobsHub/SchedulerService): the
        // ux_jobs_one_active_per_repo unique index rejects a second concurrent insert for this repo.
        // Only the unique-constraint violation (SQLITE_CONSTRAINT_UNIQUE) means "already running" —
        // any other SqliteException (disk full, corruption, …) gets its own message logged instead of
        // being masked. This sits before the method's main try/catch, so a non-matching SqliteException
        // is handled right here rather than relying on that catch.
        try { database.InsertJob(jobId, repositoryId, "archive", trigger, "running"); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            sink.Done("failed", "A job is already running for this repository.");
            return;
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Archive job {JobId} failed to start", jobId);
            sink.Done("failed", ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(repo.LocalPath))
        {
            sink.Log("No local folder configured for this repository — set one in Properties.", "warn");
            database.CompleteJob(jobId, "failed", 0, "No source folder configured.");
            sink.Done("failed", "No source folder configured.");
            return;
        }

        jobStates.Register(jobId, sink);
        sink.StartReporting();

        var gate = LockFor(repositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadWrite, sink, sink.Cts.Token);
            var mediator = provider.GetRequiredService<IMediator>();

            var uploadTier = Enum.TryParse<BlobTier>(tier, ignoreCase: true, out var bt) ? bt : BlobTier.Archive;

            var result = await mediator.Send(new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = repo.LocalPath!,
                UploadTier    = uploadTier,
                RemoveLocal   = removeLocal,
                WritePointers = writePointers,
                FastHash      = fastHash,
            }), sink.Cts.Token);

            if (result.Success)
            {
                var summary = $"Archive complete · {result.FilesUploaded} uploaded · {result.FilesDeduped} deduped · {JobFormat.Bytes(result.IncrementalStoredSize)} stored ({JobFormat.Bytes(result.IncrementalSize)} uncompressed) · {JobFormat.Bytes(result.OriginalSize)} original";
                database.CompleteJob(jobId, "completed", 100, summary);
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
                var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, result.SnapshotTime.ToString("O")));
                database.SetJobOutcome(jobId, outcomeJson);
                sink.EmitNow();                       // final absolute progress (100%) before the terminal message
                sink.Done("completed", summary, outcomeJson);
            }
            else
            {
                database.CompleteJob(jobId, "failed", 0, result.ErrorMessage);
                sink.Done("failed", result.ErrorMessage ?? "Archive failed.");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Archive job {JobId} cancelled", jobId);
            database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
            sink.Done("cancelled", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive job {JobId} failed", jobId);
            database.CompleteJob(jobId, "failed", 0, ex.Message);
            sink.Log(ex.Message, "warn");
            sink.Done("failed", ex.Message);
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            registry.Evict(repositoryId);            // snapshot may have changed → rebuild read caches
            database.ClearStatisticsCache(repositoryId); // …and discard memoized statistics for the old snapshot set
            sink.StopReporting();
            jobStates.Remove(jobId);
            sink.Cts.Dispose();
            gate.Release();
        }
    }

    public async Task RunRestoreAsync(long repositoryId, string jobId, string connectionId, string? version, IReadOnlyList<string> targetPaths, bool overwrite, bool noPointers)
    {
        var sink = new JobSink(jobId, hub);
        var startedAt = DateTimeOffset.UtcNow;
        var repo = database.GetRepository(repositoryId);
        if (repo is null) { sink.Done("failed", "Repository not found."); return; }
        // Race-proof backstop for the cooperative HasActiveJob check (JobsHub/SchedulerService): the
        // ux_jobs_one_active_per_repo unique index rejects a second concurrent insert for this repo.
        // Only the unique-constraint violation (SQLITE_CONSTRAINT_UNIQUE) means "already running" —
        // any other SqliteException (disk full, corruption, …) gets its own message logged instead of
        // being masked. This sits before the method's main try/catch, so a non-matching SqliteException
        // is handled right here rather than relying on that catch.
        try { database.InsertJob(jobId, repositoryId, "restore", "one-off", "running"); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            sink.Done("failed", "A job is already running for this repository.");
            return;
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Restore job {JobId} failed to start", jobId);
            sink.Done("failed", ex.Message);
            return;
        }

        var destination = string.IsNullOrWhiteSpace(repo.LocalPath)
            ? Path.Combine(Path.GetTempPath(), "arius-restore", repositoryId.ToString())
            : repo.LocalPath!;

        jobStates.Register(jobId, sink);
        sink.StartReporting();

        var gate = LockFor(repositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            // Inside the try (not before it): a bad LocalPath/permissions error here is now handled by
            // the catch below (marks the job "failed" + emits Done) instead of escaping this
            // fire-and-forget task and leaving the row stuck "running" — which would block all future
            // jobs for this repo under the single-active-job guard.
            Directory.CreateDirectory(destination);

            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadOnly, sink, sink.Cts.Token);
            // Cost-approval outcome for this run (set inside the ConfirmRehydration callback). The happy path is
            // in-run: the user answers and the chosen priority feeds back into THIS run — the wait is unbounded
            // (held by the still-live run) until answered or the run's token is cancelled. On decline the run is
            // cancelled. (Design §8.)
            RehydratePriority? runApprovedPriority = null;
            var costDeclined = false;
            RestoreCostEstimate? lastEstimate = null;
            CostEstimateDto? lastCostDto = null;

            var (pending, success, error) = await RunRestoreOnceAsync(
                provider, sink, jobId, destination, version, targetPaths, overwrite, noPointers,
                confirmRehydration: async (estimate, ct) =>
                {
                    // Same priority for the whole run — a later target that also needs rehydration must not
                    // re-prompt (idempotent restore reuses the already-approved answer).
                    if (runApprovedPriority is not null) return runApprovedPriority;

                    lastEstimate = estimate;
                    sink.Log(estimate.ChunksNeedingRehydration > 0
                        ? "⚠ archive-tier chunks need rehydration — awaiting cost approval"
                        : "Estimated restore cost — awaiting approval", "warn");
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
                    // Retained on the sink (not yet persisted) so a reattach while this run is still LIVE but
                    // blocked on the approval wait below can render the same cost + resume defaults via
                    // AttachToJob/GET's live-sink branch (#2/#13/#14) — jobStates keeps this sink registered
                    // until RunRestoreAsync itself returns, which does not happen until the wait resolves.
                    sink.SetPendingResume(ResumeParamsFor(estimate, version, targetPaths, destination, overwrite,
                        noPointers, priority: "Standard", autoResume: true, startedAt: DateTimeOffset.UtcNow));

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

            if (!success)
            {
                database.CompleteJob(jobId, "failed", 0, error);
                sink.Done("failed", error ?? "Restore failed.");
                return;
            }

            if (costDeclined)
            {
                database.CompleteJob(jobId, "cancelled", 0, "Restore declined at cost approval.");
                sink.Done("cancelled", "Restore declined.");
                return;
            }
            // Latent-bug fix (§7): some chunks still rehydrating — do NOT mark completed. Park as `rehydrating`
            // with resume params so the poller (Task 7) re-drives the SAME jobId until every chunk is available.
            if (pending > 0)
            {
                var priority = runApprovedPriority?.ToString() ?? "Standard";
                var resume = ResumeParamsFor(lastEstimate, version, targetPaths, destination, overwrite, noPointers,
                                             priority, autoResume: true, startedAt: DateTimeOffset.UtcNow);
                resume = sink.WithLiveRehydrationCounts(resume);   // fold live rehydration counts into resume (Step 5)
                database.SetJobStatus(jobId, "rehydrating", $"{pending} chunk(s) rehydrating");
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume)));
                sink.Log($"{pending} chunk(s) rehydrating — will auto-resume", "warn");
                // Non-terminal: no Done. The poller (Task 7) re-drives on its cadence; the finally releases the gate.
                return;
            }

            database.CompleteJob(jobId, "completed", 100, "Restore complete.");
            database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
            var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, snapshotTimestamp: null));
            database.SetJobOutcome(jobId, outcomeJson);
            sink.EmitNow();                       // final absolute progress (100%) before the terminal message
            sink.Done("completed", "Restore complete.", outcomeJson);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Restore job {JobId} cancelled", jobId);
            database.CompleteJob(jobId, "cancelled", 0, "Cancelled.");
            sink.Done("cancelled", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore job {JobId} failed", jobId);
            database.CompleteJob(jobId, "failed", 0, ex.Message);
            sink.Log(ex.Message, "warn");
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

    /// <summary>Runs the restore command over the resolved targets with the supplied cost callback, summing
    /// <see cref="RestoreResult.ChunksPendingRehydration"/>. Returns (totalPending, success, error). Shared by the
    /// initial run and <see cref="ResumeRestoreAsync"/>. The caller owns status bookkeeping + persistence.</summary>
    private async Task<(int Pending, bool Success, string? Error)> RunRestoreOnceAsync(
        ServiceProvider provider, JobSink sink, string jobId, string destination, string? version,
        IReadOnlyList<string> targetPaths, bool overwrite, bool noPointers,
        Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>> confirmRehydration,
        Func<bool>? shouldStop = null)
    {
        var mediator = provider.GetRequiredService<IMediator>();
        var targets = targetPaths.Count == 0 ? new string?[] { null } : targetPaths.Cast<string?>().ToArray();
        var totalPending = 0;

        foreach (var target in targets)
        {
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

            // A decline/timeout on any target aborts the whole job — stop processing further targets so a later
            // target cannot un-poison the run's terminal status (multi-target correctness; carried from the Task-5
            // inline break that this extraction replaces). ResumeRestoreAsync passes no predicate (non-prompting,
            // never declines/times-out).
            if (shouldStop?.Invoke() == true) break;
        }
        return (totalPending, true, null);
    }

    /// <summary>Re-drives a parked/rehydrating restore with the SAME jobId, non-prompting (honors the persisted
    /// priority — no re-charge, no connection). Loads resume params from state_json, flips the row to running for
    /// the (short) re-run, then back to rehydrating (still pending) or completed. Exempt from the single-active-job
    /// guard: it UPDATEs the existing row, never InsertJob. Design §7.</summary>
    public async Task ResumeRestoreAsync(string jobId)
    {
        var job = database.GetJob(jobId);
        // Only a still-parked job may be resumed — a cancel/complete that committed between the poller's row
        // read and this call must not resurrect a terminal job to running.
        if (job is not null && job.Status is not ("rehydrating" or "awaiting-cost")) return;
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
            // Re-check under the gate: a cancel/complete may have committed between the pre-gate status guard
            // and here. Bail (the finally releases the gate + tears down the sink) rather than resurrect a job
            // that is no longer parked. Shrinks the resurrection window to two adjacent statements.
            var underGate = database.GetJob(jobId);
            if (underGate is null || underGate.Status is not ("rehydrating" or "awaiting-cost")) return;

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
            var startedAt = job.StartedAt ?? resume.RehydrationStartedAt;
            var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, null));
            database.SetJobOutcome(jobId, outcomeJson);
            sink.EmitNow();                       // final absolute progress (100%) before the terminal message
            sink.Done("completed", "Restore complete.", outcomeJson);
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

    /// <summary>Cancels a job that has no live run to observe a token-cancel (a rehydrating job between poller ticks,
    /// or an awaiting-cost row swept as abandoned). Marks it <c>cancelled</c> in the DB AND broadcasts the terminal
    /// <c>Done</c> to its SignalR group so attached clients finalize immediately — the parked paths previously wrote
    /// the DB but never told the client (review #5). A fresh <see cref="JobSink"/> reuses the exact Done wire shape.</summary>
    public void CancelParked(string jobId, string summary = "Cancelled.")
    {
        // Only broadcast the terminal Done if our guarded cancel actually applied. If the rehydration poller won
        // the race and already completed the job, CompleteJob no-ops (row stays terminal) and we must NOT tell the
        // client it was cancelled (review #5 follow-up).
        if (database.CompleteJob(jobId, "cancelled", 0, summary))
            new JobSink(jobId, hub).Done("cancelled", summary);
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
}
