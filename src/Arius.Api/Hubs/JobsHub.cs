using System.Runtime.CompilerServices;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.AppData;
using Arius.Api.Jobs;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>
/// The single Arius realtime hub: repository entry streaming (file browser + time-travel) and the
/// archive/restore job streams with the inline cost-approval handshake. Container discovery and
/// global search are added in later phases.
/// </summary>
public sealed class JobsHub(
    RepositoryProviderRegistry registry,
    AppDatabase database,
    JobRunner jobRunner,
    RestoreApprovalRegistry approvals) : Hub
{
    /// <summary>Starts an archive; the caller's connection joins the job group before events flow.</summary>
    public async Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool noPointers)
    {
        var jobId = Guid.NewGuid().ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        _ = jobRunner.RunArchiveAsync(repositoryId, jobId, tier, removeLocal, noPointers);
        return jobId;
    }

    /// <summary>Starts a restore (empty targetPaths = whole repository).</summary>
    public async Task<string> StartRestore(long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers)
    {
        var jobId = Guid.NewGuid().ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        _ = jobRunner.RunRestoreAsync(repositoryId, jobId, version, targetPaths ?? [], overwrite, noPointers);
        return jobId;
    }

    /// <summary>Answers the restore cost modal: "standard" | "high" to proceed, anything else to decline.</summary>
    public void Approve(string jobId, string? priority)
    {
        RehydratePriority? chosen = priority?.ToLowerInvariant() switch
        {
            "standard" => RehydratePriority.Standard,
            "high"     => RehydratePriority.High,
            _          => null,
        };
        approvals.Resolve(jobId, chosen);
    }

    /// <summary>
    /// Streams the immediate children (directories + files) of a folder in a snapshot, server → client.
    /// </summary>
    /// <param name="version">Snapshot version (null/empty = latest).</param>
    /// <param name="prefix">Folder path within the repository (null/empty = root).</param>
    /// <param name="filter">Case-insensitive filename substring filter.</param>
    /// <param name="includeLocal">Overlay the repository's local folder onto the listing.</param>
    public async IAsyncEnumerable<EntryDto> StreamEntries(
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repository = database.GetRepository(repositoryId);
        var provider   = await registry.GetReadProviderAsync(repositoryId, cancellationToken);
        var mediator   = provider.GetRequiredService<IMediator>();

        var options = new ListQueryOptions
        {
            Version   = string.IsNullOrWhiteSpace(version) ? null : version,
            Prefix    = string.IsNullOrWhiteSpace(prefix) ? null : RelativePath.Parse(prefix),
            Filter    = string.IsNullOrWhiteSpace(filter) ? null : filter,
            Recursive = false,
            LocalPath = includeLocal ? repository?.LocalPath : null,
        };

        await foreach (var entry in mediator.CreateStream(new ListQuery(options), cancellationToken))
            yield return EntryMapping.ToDto(entry);
    }
}
