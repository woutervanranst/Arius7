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
/// The Arius realtime hub. Each method forwards to the <c>IHubHandler</c> class of the same name under
/// <c>Features/</c>, received as a method-injected service; keep logic out of the hub.
/// </summary>
/// <remarks>Every handler parameter needs <c>[FromServices]</c>: SignalR infers a service only for a parameter without
/// attributes, and the compiler adds nullability attributes to some parameters, which would make the handler an
/// expected wire argument.</remarks>
public sealed class JobsHub : Hub
{
    public IAsyncEnumerable<string> StreamContainers(long accountId, string? accountName, string? accountKey, [FromServices] Features.Accounts.StreamContainers handler, CancellationToken cancellationToken)
        => handler.HandleAsync(accountId, accountName, accountKey, cancellationToken);

    public IAsyncEnumerable<EntryDto> StreamEntries(long repositoryId, string? version, string? prefix, string? filter, bool includeLocal, [FromServices] Features.Browse.StreamEntries handler, CancellationToken cancellationToken)
        => handler.HandleAsync(repositoryId, version, prefix, filter, includeLocal, cancellationToken);

    public IAsyncEnumerable<SearchHitDto> SearchAll(string query, [FromServices] Features.Browse.SearchAll handler, CancellationToken cancellationToken)
        => handler.HandleAsync(query, cancellationToken);

    public Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash, [FromServices] Features.Archive.StartArchive handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, tier, removeLocal, writePointers, fastHash);

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

    public Task<string> StartDownload(long repositoryId, string? version, DownloadFileRequest[]? files, [FromServices] Features.Downloads.StartDownload handler)
        => handler.HandleAsync(Context.ConnectionId, repositoryId, version, files);

    public Task<JobAttachState?> AttachToJob(string jobId, [FromServices] Features.Jobs.AttachToJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task DetachFromJob(string jobId, [FromServices] Features.Jobs.DetachFromJob handler)
        => handler.HandleAsync(Context.ConnectionId, jobId);

    public Task CancelJob(string jobId, [FromServices] Features.Jobs.CancelJob handler)
        => handler.HandleAsync(jobId);
}
