using System.Runtime.CompilerServices;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.AppData;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Mediator;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>
/// The single Arius realtime hub. Phase 2 exposes repository entry streaming (the file browser +
/// time-travel); archive/restore job streams, the cost-approval handshake, container discovery and
/// global search are added in later phases.
/// </summary>
public sealed class JobsHub(RepositoryProviderRegistry registry, AppDatabase database) : Hub
{
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
