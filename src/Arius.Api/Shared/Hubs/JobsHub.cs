using Arius.Api.Features.Accounts;
using Arius.Api.Features.Archive;
using Arius.Api.Features.Browse;
using Arius.Api.Features.Downloads;
using Arius.Api.Features.Jobs;
using Arius.Api.Features.Restore;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Shared.Hubs;

/// <summary>
/// The single Arius realtime hub: repository entry streaming (file browser + time-travel) and the
/// archive/restore job streams with the inline cost-approval handshake. A pure dispatcher: each hub method's
/// behaviour lives in the <c>IHubHandler</c> feature class of the same name under <c>Features/</c>, which the hub receives
/// as a method-injected service.
/// </summary>
public sealed class JobsHub : Hub
{
    // ── Accounts ──────────────────────────────────────────────────────────────

    public IAsyncEnumerable<string> StreamContainers(long accountId, string? accountName, string? accountKey, Features.Accounts.StreamContainers handler, CancellationToken cancellationToken)
        => handler.HandleAsync(accountId, accountName, accountKey, cancellationToken);

    // ── Browse ────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<EntryDto> StreamEntries(long repositoryId, string? version, string? prefix, string? filter, bool includeLocal, Features.Browse.StreamEntries handler, CancellationToken cancellationToken)
        => handler.HandleAsync(repositoryId, version, prefix, filter, includeLocal, cancellationToken);

    public IAsyncEnumerable<SearchHitDto> SearchAll(string query, Features.Browse.SearchAll handler, CancellationToken cancellationToken)
        => handler.HandleAsync(query, cancellationToken);

    // ── Archive ───────────────────────────────────────────────────────────────

    public Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash, Features.Archive.StartArchive handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, tier, removeLocal, writePointers, fastHash);

    // ── Restore ───────────────────────────────────────────────────────────────

    public Task<string> StartRestore(long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers, Features.Restore.StartRestore handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, version, targetPaths, overwrite, noPointers);

    public Task ApproveRestore(string jobId, string? priority, Features.Restore.ApproveRestore handler)
        => handler.HandleAsync(jobId, priority);

    public Task DeclineRestore(string jobId, Features.Restore.DeclineRestore handler)
        => handler.HandleAsync(jobId);

    public Task SetAutoResume(string jobId, bool autoResume, Features.Restore.SetAutoResume handler)
        => handler.HandleAsync(jobId, autoResume);

    public Task ResumeRestore(string jobId, Features.Restore.ResumeRestore handler)
        => handler.HandleAsync(jobId);

    // ── Downloads ─────────────────────────────────────────────────────────────

    public Task<string> StartDownload(long repositoryId, string? version, DownloadFileRequest[]? files, Features.Downloads.StartDownload handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, version, files);

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public Task<JobAttachState?> AttachToJob(string jobId, Features.Jobs.AttachToJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task DetachFromJob(string jobId, Features.Jobs.DetachFromJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task CancelJob(string jobId, Features.Jobs.CancelJob handler)
        => handler.HandleAsync(jobId);
}
