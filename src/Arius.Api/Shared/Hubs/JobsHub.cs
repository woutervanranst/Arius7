using Arius.Api.Features.Accounts;
using Arius.Api.Features.Archive;
using Arius.Api.Features.Browse;
using Arius.Api.Features.Downloads;
using Arius.Api.Features.Jobs;
using Arius.Api.Features.Restore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Shared.Hubs;

/// <summary>
/// The single Arius realtime hub: repository entry streaming (file browser + time-travel) and the
/// archive/restore job streams with the inline cost-approval handshake. A pure dispatcher: each hub method's
/// behaviour lives in the <c>IHubHandler</c> feature class of the same name under <c>Features/</c>, which the hub receives
/// as a method-injected service.
/// </summary>
/// <remarks>Every handler parameter carries <c>[FromServices]</c>: SignalR only infers a service for a parameter
/// without attributes, and the compiler adds a nullability attribute to some of them, which would turn the handler
/// into an expected wire argument.</remarks>
public sealed class JobsHub : Hub
{
    // ── Accounts ──────────────────────────────────────────────────────────────

    public IAsyncEnumerable<string> StreamContainers(long accountId, string? accountName, string? accountKey, [FromServices] Features.Accounts.StreamContainers handler, CancellationToken cancellationToken)
        => handler.HandleAsync(accountId, accountName, accountKey, cancellationToken);

    // ── Browse ────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<EntryDto> StreamEntries(long repositoryId, string? version, string? prefix, string? filter, bool includeLocal, [FromServices] Features.Browse.StreamEntries handler, CancellationToken cancellationToken)
        => handler.HandleAsync(repositoryId, version, prefix, filter, includeLocal, cancellationToken);

    public IAsyncEnumerable<SearchHitDto> SearchAll(string query, [FromServices] Features.Browse.SearchAll handler, CancellationToken cancellationToken)
        => handler.HandleAsync(query, cancellationToken);

    // ── Archive ───────────────────────────────────────────────────────────────

    public Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash, [FromServices] Features.Archive.StartArchive handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, tier, removeLocal, writePointers, fastHash);

    // ── Restore ───────────────────────────────────────────────────────────────

    public Task<string> StartRestore(long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers, [FromServices] Features.Restore.StartRestore handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, version, targetPaths, overwrite, noPointers);

    public Task ApproveRestore(string jobId, string? priority, [FromServices] Features.Restore.ApproveRestore handler)
        => handler.HandleAsync(jobId, priority);

    public Task DeclineRestore(string jobId, [FromServices] Features.Restore.DeclineRestore handler)
        => handler.HandleAsync(jobId);

    public Task SetAutoResume(string jobId, bool autoResume, [FromServices] Features.Restore.SetAutoResume handler)
        => handler.HandleAsync(jobId, autoResume);

    public Task ResumeRestore(string jobId, [FromServices] Features.Restore.ResumeRestore handler)
        => handler.HandleAsync(jobId);

    // ── Downloads ─────────────────────────────────────────────────────────────

    public Task<string> StartDownload(long repositoryId, string? version, DownloadFileRequest[]? files, [FromServices] Features.Downloads.StartDownload handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, version, files);

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public Task<JobAttachState?> AttachToJob(string jobId, [FromServices] Features.Jobs.AttachToJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task DetachFromJob(string jobId, [FromServices] Features.Jobs.DetachFromJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task CancelJob(string jobId, [FromServices] Features.Jobs.CancelJob handler)
        => handler.HandleAsync(jobId);
}
