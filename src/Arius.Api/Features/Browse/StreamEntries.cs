using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Features.Browse;

/// <summary>
/// Hub <c>StreamEntries</c>: streams the immediate children (directories and files) of a folder in a snapshot.
/// </summary>
public sealed class StreamEntries(AppDatabase database, RepositoryProviderRegistry registry) : IHubHandler
{
    /// <param name="version">Snapshot version; null or empty for the latest.</param>
    /// <param name="prefix">Folder path within the repository; null or empty for the root.</param>
    /// <param name="filter">Case-insensitive filename substring filter.</param>
    /// <param name="includeLocal">Overlay the repository's local folder onto the listing.</param>
    public async IAsyncEnumerable<EntryDto> HandleAsync(
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repository = database.GetRepository(repositoryId);

        // A container that does not exist yet (no archive has run) has no entries: return an empty stream
        // instead of a hub error.
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
