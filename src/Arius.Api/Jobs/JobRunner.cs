using System.Collections.Concurrent;
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Hubs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
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
            sink.Log($"Connecting to container {repo.Container}…", "meta");
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

            if (result.Success)
            {
                var summary = $"Archive complete · {result.FilesUploaded} uploaded · {result.FilesDeduped} deduped · {JobFormat.Bytes(result.IncrementalStoredSize)} stored ({JobFormat.Bytes(result.IncrementalSize)} uncompressed) · {JobFormat.Bytes(result.OriginalSize)} original";
                database.CompleteJob(jobId, "completed", 100, summary);
                database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
                database.SetJobOutcome(jobId, JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, result.SnapshotTime.ToString("O"))));
                sink.Done("completed", summary);
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

            sink.Log($"Connecting to container {repo.Container}…", "meta");
            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadOnly, sink, sink.Cts.Token);
            var mediator = provider.GetRequiredService<IMediator>();

            // Empty collection = whole-repository restore; otherwise restore each collected path.
            var targets = targetPaths.Count == 0 ? new string?[] { null } : targetPaths.Cast<string?>().ToArray();

            foreach (var target in targets)
            {
                sink.Log(target is null ? "Resolving whole repository…" : $"Resolving {target}…", "meta");
                var result = await mediator.Send(new RestoreCommand(new RestoreOptions
                {
                    RootDirectory = destination,
                    Version       = version,
                    TargetPath    = target is null ? null : RelativePath.Parse(target),
                    Overwrite     = overwrite,
                    NoPointers    = noPointers,
                    ConfirmRehydration = async (estimate, ct) =>
                    {
                        sink.Log(estimate.ChunksNeedingRehydration > 0
                            ? "⚠ archive-tier chunks need rehydration — awaiting cost approval"
                            : "Estimated restore cost — awaiting approval", "warn");
                        sink.Cost(new
                        {
                            chunksAvailable          = estimate.ChunksAvailable + estimate.ChunksAlreadyRehydrated,
                            chunksNeedingRehydration = estimate.ChunksNeedingRehydration,
                            bytesNeedingRehydration  = estimate.BytesNeedingRehydration,
                            downloadBytes            = estimate.DownloadBytes,
                            totalStandard            = estimate.TotalStandard,
                            totalHigh                = estimate.TotalHigh,
                        });
                        var priority = await approvals.Register(jobId, connectionId);
                        sink.Log(priority is null ? "Restore declined." : $"Approved · {priority} priority", priority is null ? "warn" : "info");
                        return priority;
                    },
                }), sink.Cts.Token);

                if (!result.Success)
                {
                    database.CompleteJob(jobId, "failed", 0, result.ErrorMessage);
                    sink.Done("failed", result.ErrorMessage ?? "Restore failed.");
                    return;
                }
            }

            database.CompleteJob(jobId, "completed", 100, "Restore complete.");
            database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UtcNow, resume: null)));
            database.SetJobOutcome(jobId, JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, snapshotTimestamp: null)));
            sink.Done("completed", "Restore complete.");
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
}
