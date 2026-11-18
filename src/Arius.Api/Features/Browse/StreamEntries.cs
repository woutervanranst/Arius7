using System.Runtime.CompilerServices;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Hubs;

/// <summary>
/// Hub <c>StreamEntries</c> — streams the immediate children (directories + files) of a folder in a snapshot,
/// server → client.
/// </summary>
internal static class StreamEntries
{
    /// <param name="version">Snapshot version (null/empty = latest).</param>
    /// <param name="prefix">Folder path within the repository (null/empty = root).</param>
    /// <param name="filter">Case-insensitive filename substring filter.</param>
    /// <param name="includeLocal">Overlay the repository's local folder onto the listing.</param>
    public static async IAsyncEnumerable<EntryDto> HandleAsync(
        AppDatabase database,
        RepositoryProviderRegistry registry,
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repository = database.GetRepository(repositoryId);

        // A repository whose container hasn't been created yet (no archive has run) has no entries
        // by definition — degrade to an empty stream rather than surfacing a hub exception.
        ServiceProvider provider;
        try
        {
            provider = await registry.GetReadProviderAsync(repositoryId, cancellationToken);
        }
        catch (PreflightException ex) when (ex.ErrorKind == PreflightErrorKind.ContainerNotFound)
        {
            yield break;
        }

        var mediator = provider.GetRequiredService<IMediator>();

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
