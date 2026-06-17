using System.Collections.Concurrent;
using Arius.Api.Composition;
using Arius.Api.AppData;
using Arius.Api.Hubs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.SignalR;

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
    ILogger<JobRunner> logger)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _repoLocks = new();

    private SemaphoreSlim LockFor(long repositoryId) => _repoLocks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));

    public async Task RunArchiveAsync(long repositoryId, string jobId, string tier, bool removeLocal, bool noPointers, string trigger = "one-off")
    {
        var sink = new JobSink(jobId, hub);
        var repo = database.GetRepository(repositoryId);
        if (repo is null) { sink.Done("failed", "Repository not found."); return; }
        database.InsertJob(jobId, repositoryId, "archive", trigger, "running");
        if (string.IsNullOrWhiteSpace(repo.LocalPath))
        {
            sink.Log("No local folder configured for this repository — set one in Properties.", "warn");
            database.CompleteJob(jobId, "failed", 0, "No source folder configured.");
            sink.Done("failed", "No source folder configured.");
            return;
        }

        var gate = LockFor(repositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            sink.Log($"Connecting to container {repo.Container}…", "meta");
            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadWrite, sink, CancellationToken.None);
            var mediator = provider.GetRequiredService<IMediator>();

            var uploadTier = Enum.TryParse<BlobTier>(tier, ignoreCase: true, out var bt) ? bt : BlobTier.Archive;
            sink.Log($"Scanning {repo.LocalPath} …", "meta");

            var result = await mediator.Send(new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = repo.LocalPath!,
                UploadTier    = uploadTier,
                RemoveLocal   = removeLocal,
                NoPointers    = noPointers,
            }));

            if (result.Success)
            {
                var summary = $"Archive complete · {result.FilesUploaded} uploaded · {result.FilesDeduped} deduped · {JobFormat.Bytes(result.TotalSize)}";
                database.CompleteJob(jobId, "completed", 100, summary);
                sink.Done("completed", summary);
            }
            else
            {
                database.CompleteJob(jobId, "failed", 0, result.ErrorMessage);
                sink.Done("failed", result.ErrorMessage ?? "Archive failed.");
            }
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
            registry.Evict(repositoryId);   // snapshot may have changed → rebuild read caches
            gate.Release();
        }
    }

    public async Task RunRestoreAsync(long repositoryId, string jobId, string connectionId, string? version, IReadOnlyList<string> targetPaths, bool overwrite, bool noPointers)
    {
        var sink = new JobSink(jobId, hub);
        var repo = database.GetRepository(repositoryId);
        if (repo is null) { sink.Done("failed", "Repository not found."); return; }
        database.InsertJob(jobId, repositoryId, "restore", "one-off", "running");

        var destination = string.IsNullOrWhiteSpace(repo.LocalPath)
            ? Path.Combine(Path.GetTempPath(), "arius-restore", repositoryId.ToString())
            : repo.LocalPath!;
        Directory.CreateDirectory(destination);

        var gate = LockFor(repositoryId);
        await gate.WaitAsync();
        ServiceProvider? provider = null;
        try
        {
            sink.Log($"Connecting to container {repo.Container}…", "meta");
            provider = await registry.CreateJobProviderAsync(repositoryId, PreflightMode.ReadOnly, sink, CancellationToken.None);
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
                        sink.Log("⚠ archive-tier chunks need rehydration — awaiting approval", "warn");
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
                }));

                if (!result.Success)
                {
                    database.CompleteJob(jobId, "failed", 0, result.ErrorMessage);
                    sink.Done("failed", result.ErrorMessage ?? "Restore failed.");
                    return;
                }
            }

            database.CompleteJob(jobId, "completed", 100, "Restore complete.");
            sink.Done("completed", "Restore complete.");
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
            gate.Release();
        }
    }
}
