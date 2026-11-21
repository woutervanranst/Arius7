using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Arius.Api.Features.Downloads;
using Arius.Api.Features.Jobs;
using Arius.Api.Features.Schedules;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hubs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.Jobs;

/// <summary>
/// Runs archive, restore and download jobs, each in its own service provider, and handles the restore
/// cost-approval handshake. Jobs are serialized per repository so two jobs never share a repository's
/// on-disk state.
/// </summary>
public sealed class JobRunner(
    RepositoryProviderRegistry registry,
    AppDatabase database,
    IHubContext<JobsHub> hub,
    RestoreApprovalRegistry approvals,
    JobStateRegistry jobStates,
    DownloadStaging staging,
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
        // The ux_jobs_one_active_per_repo unique index backs up the HasActiveJob check against concurrent starts.
        // Only a unique-constraint violation (SQLITE_CONSTRAINT_UNIQUE) means a job is already running.
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
            FailOrCancel(sink, jobId, "failed", "No source folder configured.", "No source folder configured.");
            return;
        }

        jobStates.Register(jobId, sink);
        // Before StartReporting, so [ETA] tracing also covers the provider-build phase.
        registry.AttachJobDiagnostics(sink, repositoryId);
        sink.StartReporting();
        sink.SetPhase("scan");   // later phases advance from Core events

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
                // Per-chunk upload progress, so uploaded bytes, rate and ETA advance while a chunk uploads.
                CreateUploadProgress = (chunkHash, size) => new Progress<long>(cumulative => sink.ReportUploadStreamed(chunkHash, cumulative)),
            }), sink.Cts.Token);

            if (result.Success)
            {
                var summary = $"Archive complete · {result.FilesUploaded} uploaded · {result.FilesDeduped} deduped · {JobFormat.Bytes(result.IncrementalStoredSize)} stored ({JobFormat.Bytes(result.IncrementalSize)} uncompressed) · {JobFormat.Bytes(result.OriginalSize)} original";
                CompleteAndBroadcast(sink, jobId, summary, startedAt, result.SnapshotTime.ToString("O"));
            }
            else
            {
                FailOrCancel(sink, jobId, "failed", result.ErrorMessage, result.ErrorMessage ?? "Archive failed.");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Archive job {JobId} cancelled", jobId);
            FailOrCancel(sink, jobId, "cancelled", "Cancelled.", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive job {JobId} failed", jobId);
            sink.Log(ex.Message, "warn");
            FailOrCancel(sink, jobId, "failed", ex.Message, ex.Message);
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            registry.Evict(repositoryId);                // the snapshot set may have changed: rebuild the read provider
            database.ClearStatisticsCache(repositoryId); // and drop statistics computed for the old snapshot set
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
        if (!TryInsertRestoreJob(sink, repositoryId, jobId, "restore", "Restore")) return;

        var destination = string.IsNullOrWhiteSpace(repo.LocalPath)
            ? Path.Combine(Path.GetTempPath(), "arius-restore", repositoryId.ToString())
            : repo.LocalPath!;

        await RunRestoreJobAsync(sink, repositoryId, jobId, startedAt, version, targetPaths, destination, overwrite, noPointers, "Restore");
    }

    /// <summary>Runs a download job: restores <paramref name="files"/> from snapshot <paramref name="version"/> into
    /// the job's staging folder, never the repository's local folder. Each restored file is marked ready as it lands.</summary>
    public async Task RunDownloadAsync(long repositoryId, string jobId, string version, IReadOnlyList<DownloadFile> files)
    {
        var sink = new JobSink(jobId, hub);
        var startedAt = DateTimeOffset.UtcNow;
        if (database.GetRepository(repositoryId) is null) { sink.Done("failed", "Repository not found."); return; }
        if (!TryInsertRestoreJob(sink, repositoryId, jobId, "download", "Download")) return;

        try { database.InsertDownloadFiles(jobId, files); }
        catch (Exception ex)
        {
            // The job row is already inserted as running; mark it failed, or it blocks the repository until restart.
            logger.LogError(ex, "Download job {JobId} failed to record its files", jobId);
            FailOrCancel(sink, jobId, "failed", ex.Message, ex.Message);
            return;
        }
        var anyReady = false;
        sink.FileRestored = path => { database.MarkDownloadFileReady(jobId, path); anyReady = true; };

        await RunRestoreJobAsync(sink, repositoryId, jobId, startedAt, version, files.Select(f => f.Path).ToArray(),
            staging.PathFor(jobId), overwrite: false, noPointers: true, "Download");

        // A first pass that ends with nothing ready (declined, cancelled, failed) has nothing to offer, so delete its
        // staging folder now rather than after the clean-up period. A parked (rehydrating) job keeps its folder;
        // later exits are left to DownloadCleaner.
        if (!anyReady && database.GetJob(jobId) is { } job && JobStatuses.Terminal.Contains(job.Status))
        {
            try { staging.Delete(jobId); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove the staging folder of download {JobId}", jobId); }
        }
    }

    // The ux_jobs_one_active_per_repo unique index backs up the HasActiveJob check against concurrent starts.
    // Only a unique-constraint violation (SQLITE_CONSTRAINT_UNIQUE) means a job is already running.
    private bool TryInsertRestoreJob(JobSink sink, long repositoryId, string jobId, string kind, string noun)
    {
        try { database.InsertJob(jobId, repositoryId, kind, "one-off", "running"); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            sink.Done("failed", "A job is already running for this repository.");
            return false;
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "{Noun} job {JobId} failed to start", noun, jobId);
            sink.Done("failed", ex.Message);
            return false;
        }
        return true;
    }

    private async Task RunRestoreJobAsync(JobSink sink, long repositoryId, string jobId, DateTimeOffset startedAt, string? version,
        IReadOnlyList<string> targetPaths, string destination, bool overwrite, bool noPointers, string noun)
    {
        jobStates.Register(jobId, sink);
        // Before StartReporting, so [ETA] tracing also covers the provider-build phase.
        registry.AttachJobDiagnostics(sink, repositoryId);
        sink.StartReporting();

        var gate = LockFor(repositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            // Inside the try so a path or permission error fails the job instead of escaping this fire-and-forget
            // task and leaving the row running, which would block the repository.
            Directory.CreateDirectory(destination);

            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadOnly, sink, sink.Cts.Token);
            // Set by the ConfirmRehydration callback. The run waits for the answer until it is cancelled; an
            // approved priority applies to this same run.
            RehydratePriority? runApprovedPriority = null;
            var costDeclined = false;
            RestoreCostEstimate? lastEstimate = null;

            var (pending, success, error) = await RunRestoreOnceAsync(
                provider, sink, jobId, destination, version, targetPaths, overwrite, noPointers,
                confirmRehydration: async (estimate, ct) =>
                {
                    lastEstimate = estimate;

                    // With every chunk online there is no priority to pick, so a cost below the limit needs no approval;
                    // anything that rehydrates still asks.
                    if (estimate.ChunksNeedingRehydration == 0 && estimate.ChunksPendingRehydration == 0
                        && estimate.TotalStandard < (double)database.GetCostSettings().AutoAcceptBelow)
                    {
                        sink.Log(string.Create(CultureInfo.InvariantCulture,
                            $"Estimated cost €{estimate.TotalStandard:0.0000} is below the auto-accept limit — continuing without approval."));
                        return RehydratePriority.Standard;
                    }

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
                    // Prime before sink.Cost lets the client answer, so an early answer is not lost.
                    approvals.Prime(jobId);
                    sink.Cost(costDto);
                    // Kept on the sink so a client reattaching while the run awaits approval sees the same cost
                    // and resume settings.
                    sink.SetPendingResume(ResumeParamsFor(estimate, version, targetPaths, destination, overwrite,
                        noPointers, priority: "Standard", autoResume: true, startedAt: DateTimeOffset.UtcNow));

                    database.SetJobStatus(jobId, "awaiting-cost", "Awaiting cost approval");
                    sink.SetStatus("awaiting-cost");
                    sink.SetPhase("confirm-cost");

                    var priority = await approvals.RegisterAsync(jobId, ct);
                    if (priority is not null)
                    {
                        sink.ClearPending();
                        runApprovedPriority = priority;
                        database.SetJobStatus(jobId, "running");
                        sink.SetStatus("running");
                        return priority;
                    }

                    costDeclined = true;
                    sink.Log($"{noun} declined.", "warn");
                    return null;
                });

            if (!success)
            {
                FailOrCancel(sink, jobId, "failed", error, error ?? $"{noun} failed.");
                return;
            }

            if (costDeclined)
            {
                FailOrCancel(sink, jobId, "cancelled", $"{noun} declined at cost approval.", $"{noun} declined.");
                return;
            }
            // Chunks are still rehydrating: park the job with its resume parameters so the poller re-runs it under
            // the same job id until every chunk is available.
            if (pending > 0)
            {
                var priority = runApprovedPriority?.ToString() ?? "Standard";
                var resume = ResumeParamsFor(lastEstimate, version, targetPaths, destination, overwrite, noPointers,
                                             priority, autoResume: true, startedAt: DateTimeOffset.UtcNow);
                resume = sink.WithLiveRehydrationCounts(resume);
                database.SetJobStatus(jobId, "rehydrating", $"{pending} chunk(s) rehydrating");
                sink.SetStatus("rehydrating");
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume)));
                sink.Log($"{pending} chunk(s) rehydrating — will auto-resume", "warn");
                // Parked, not finished: no Done is sent.
                return;
            }

            CompleteAndBroadcast(sink, jobId, $"{noun} complete.", startedAt, snapshotTimestamp: null);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("{Noun} job {JobId} cancelled", noun, jobId);
            FailOrCancel(sink, jobId, "cancelled", "Cancelled.", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Noun} job {JobId} failed", noun, jobId);
            sink.Log(ex.Message, "warn");
            FailOrCancel(sink, jobId, "failed", ex.Message, ex.Message);
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

    /// <summary>Runs one restore pass; an empty <paramref name="targetPaths"/> restores the whole repository. The
    /// caller owns the job status and persisted state.</summary>
    private async Task<(int Pending, bool Success, string? Error)> RunRestoreOnceAsync(
        ServiceProvider provider, JobSink sink, string jobId, string destination, string? version,
        IReadOnlyList<string> targetPaths, bool overwrite, bool noPointers,
        Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>> confirmRehydration)
    {
        var mediator = provider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new RestoreCommand(new RestoreOptions
        {
            RootDirectory      = destination,
            Version            = version,
            TargetPaths        = targetPaths.Select(RelativePath.Parse).ToArray(),
            Overwrite          = overwrite,
            NoPointers         = noPointers,
            ConfirmRehydration = confirmRehydration,
            // Large files report progress while they download, so restored bytes, rate and ETA advance continuously.
            // Files in a tar chunk report once per file, so the tar's bytes are not counted twice.
            CreateLargeFileDownloadProgress = (relativePath, size) => new Progress<long>(c => sink.ReportRestoreStreamed(relativePath.ToString(), c)),
        }), sink.Cts.Token);

        return result.Success
            ? (result.ChunksPendingRehydration, true, null)
            : (0, false, result.ErrorMessage);
    }

    /// <summary>Re-runs a parked restore under the same job id with its persisted priority, without a cost prompt or
    /// a client connection. Marks the row running for the pass, then rehydrating again or completed. Not subject to
    /// the single-active-job index, because it updates the existing row instead of inserting one.</summary>
    public async Task ResumeRestoreAsync(string jobId)
    {
        var job = database.GetJob(jobId);
        // Only a parked job may be resumed; a job cancelled or completed since the poller read it stays terminal.
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
        var registered = false;
        var noun = job.Kind == "download" ? "Download" : "Restore";

        var gate = LockFor(job.RepositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            // Re-check under the gate: the job may have been cancelled or completed while waiting for it.
            var underGate = database.GetJob(jobId);
            if (underGate is null || underGate.Status is not ("rehydrating" or "awaiting-cost")) return;

            // Register only once this run holds the gate, so the registry never holds a sink of a run that is
            // still waiting for it.
            jobStates.Register(jobId, sink);
            registered = true;   // before StartReporting, so a throw there is still cleaned up in finally
            // Before StartReporting, so [ETA] tracing also covers the provider-build phase.
            registry.AttachJobDiagnostics(sink, job.RepositoryId);
            sink.StartReporting();

            // Only a row that is still parked moves to running. A cancel after the re-check above either marked the
            // row cancelled, so this update fails and the run stops, or found the registered sink and cancelled its token.
            if (!database.TryResumeToRunning(jobId, $"Resuming {noun.ToLowerInvariant()}…")) return;
            sink.SetStatus("running");
            provider = await registry.CreateJobProviderAsync(job.RepositoryId, PreflightMode.ReadOnly, sink, sink.Cts.Token);

            // A download always restores into its staging folder, whatever destination was persisted.
            var destination = resume.Destination;
            if (job.Kind == "download")
            {
                destination = staging.PathFor(jobId);
                // Only files restored by an earlier pass are complete; anything else is a partial write. Remove it
                // first, since the pass runs with Overwrite = false.
                var files = database.ListDownloadFiles(jobId);
                staging.PruneUnready(jobId, files.Where(f => f.Ready).Select(f => f.Path).ToHashSet());
                sink.FileRestored = path => database.MarkDownloadFileReady(jobId, path);
            }

            var persistedPriority = resume.Priority == "High" ? RehydratePriority.High : RehydratePriority.Standard;
            var (pending, success, error) = await RunRestoreOnceAsync(
                provider, sink, jobId, destination, resume.Version, resume.TargetPaths,
                resume.Overwrite, resume.NoPointers,
                confirmRehydration: (_, _) => Task.FromResult<RehydratePriority?>(persistedPriority));

            if (!success)
            {
                FailOrCancel(sink, jobId, "failed", error, error ?? $"{noun} failed.");
                return;
            }
            if (pending > 0)
            {
                var next = resume with { LastRunAt = DateTimeOffset.UtcNow };
                next = sink.WithLiveRehydrationCounts(next);
                database.SetJobStatus(jobId, "rehydrating", $"{pending} chunk(s) rehydrating");
                sink.SetStatus("rehydrating");
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, next)));
                return;
            }
            CompleteAndBroadcast(sink, jobId, $"{noun} complete.", job.StartedAt ?? resume.RehydrationStartedAt, null);
        }
        catch (OperationCanceledException)
        {
            FailOrCancel(sink, jobId, "cancelled", "Cancelled.", "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Noun} resume {JobId} failed", noun, jobId);
            FailOrCancel(sink, jobId, "failed", ex.Message, ex.Message);
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            if (registered) { sink.StopReporting(); jobStates.Remove(jobId); }
            sink.Cts.Dispose();
            gate.Release();
        }
    }

    /// <summary>Cancels a job with no live run (a rehydrating job between poller ticks, or an abandoned cost prompt):
    /// marks it <c>cancelled</c> and sends <c>Done</c> so attached clients update immediately.</summary>
    public void CancelParked(string jobId, string summary = "Cancelled.")
    {
        // Send Done only if the cancel applied: if the poller completed the job first, the client must not be told
        // it was cancelled.
        if (database.CompleteJob(jobId, "cancelled", 0, summary))
            new JobSink(jobId, hub).Done("cancelled", summary);
    }

    /// <summary>Marks the job <c>completed</c> and, only if no concurrent cancel ended it first, persists its final
    /// state and outcome and sends the final progress and <c>Done</c>, so they never contradict the stored status.</summary>
    private void CompleteAndBroadcast(JobSink sink, string jobId, string summary, DateTimeOffset startedAt, string? snapshotTimestamp)
    {
        if (!database.CompleteJob(jobId, "completed", 100, summary)) return;   // another writer already ended the job
        database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
        // camelCase: the string reaches the client verbatim (GET /jobs and Done) and is parsed there as a JobOutcome.
        var outcomeJson = JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, snapshotTimestamp), JsonSerializerOptions.Web);
        database.SetJobOutcome(jobId, outcomeJson);
        sink.EmitNow();                       // final progress before Done
        sink.Done("completed", summary, outcomeJson);
    }

    /// <summary>Marks the job <c>failed</c> or <c>cancelled</c> and sends <c>Done</c> only if no other writer ended
    /// the job first.</summary>
    private void FailOrCancel(JobSink sink, string jobId, string status, string? detail, string doneMessage)
    {
        if (database.CompleteJob(jobId, status, 0, detail))
            sink.Done(status, doneMessage);
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
