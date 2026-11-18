using Arius.Api.Features.Accounts;
using Arius.Api.Features.Archive;
using Arius.Api.Features.Browse;
using Arius.Api.Features.Downloads;
using Arius.Api.Features.Jobs;
using Arius.Api.Features.Restore;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Shared.Hubs;

/// <summary>
/// The single Arius realtime hub: repository entry streaming (file browser + time-travel) and the
/// archive/restore job streams with the inline cost-approval handshake. A pure dispatcher: each hub method's
/// behaviour lives in the feature file of the same name under <c>Features/</c>.
/// </summary>
public sealed class JobsHub(
    RepositoryProviderRegistry registry,
    AppDatabase database,
    JobRunner jobRunner,
    RestoreApprovalRegistry approvals,
    JobStateRegistry jobStates,
    SecretProtector secrets,
    IBlobServiceFactory blobServiceFactory) : Hub
{
    // ── Accounts ──────────────────────────────────────────────────────────────

    public IAsyncEnumerable<string> StreamContainers(long accountId, string? accountName, string? accountKey, CancellationToken cancellationToken)
        => Features.Accounts.StreamContainers.HandleAsync(database, secrets, blobServiceFactory, accountId, accountName, accountKey, cancellationToken);

    // ── Browse ────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<EntryDto> StreamEntries(long repositoryId, string? version, string? prefix, string? filter, bool includeLocal, CancellationToken cancellationToken)
        => Features.Browse.StreamEntries.HandleAsync(database, registry, repositoryId, version, prefix, filter, includeLocal, cancellationToken);

    public IAsyncEnumerable<SearchHitDto> SearchAll(string query, CancellationToken cancellationToken)
        => Features.Browse.SearchAll.HandleAsync(database, registry, query, cancellationToken);

    // ── Archive ───────────────────────────────────────────────────────────────

    public Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash)
        => Features.Archive.StartArchive.HandleAsync(this, database, jobRunner, repositoryId, tier, removeLocal, writePointers, fastHash);

    // ── Restore ───────────────────────────────────────────────────────────────

    public Task<string> StartRestore(long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers)
        => Features.Restore.StartRestore.HandleAsync(this, database, jobRunner, repositoryId, version, targetPaths, overwrite, noPointers);

    public Task ApproveRestore(string jobId, string? priority)
        => Features.Restore.ApproveRestore.HandleAsync(approvals, jobId, priority);

    public Task DeclineRestore(string jobId)
        => Features.Restore.DeclineRestore.HandleAsync(approvals, jobRunner, jobId);

    public Task SetAutoResume(string jobId, bool autoResume)
        => Features.Restore.SetAutoResume.HandleAsync(database, jobRunner, jobId, autoResume);

    public Task ResumeRestore(string jobId)
        => Features.Restore.ResumeRestore.HandleAsync(jobRunner, jobId);

    // ── Downloads ─────────────────────────────────────────────────────────────

    public Task<string> StartDownload(long repositoryId, string? version, DownloadFileRequest[]? files)
        => Features.Downloads.StartDownload.HandleAsync(this, database, jobRunner, repositoryId, version, files);

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public Task<JobAttachState?> AttachToJob(string jobId)
        => Features.Jobs.AttachToJob.HandleAsync(this, database, jobStates, jobId);

    public Task DetachFromJob(string jobId)
        => Features.Jobs.DetachFromJob.HandleAsync(this, jobId);

    public Task CancelJob(string jobId)
        => Features.Jobs.CancelJob.HandleAsync(approvals, jobStates, jobRunner, jobId);
}
