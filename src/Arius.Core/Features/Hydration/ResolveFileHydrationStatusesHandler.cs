using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.Hydration;

public sealed class ResolveFileHydrationStatusesHandler : IStreamQueryHandler<ResolveFileHydrationStatusesCommand, FileHydrationStatusResult>
{
    private readonly IBlobContainerService _blobs;
    private readonly ChunkIndexService _index;
    private readonly ILogger<ResolveFileHydrationStatusesHandler> _logger;

    public ResolveFileHydrationStatusesHandler(
        IBlobContainerService blobs,
        ChunkIndexService index,
        ILogger<ResolveFileHydrationStatusesHandler> logger)
    {
        _blobs = blobs;
        _index = index;
        _logger = logger;
    }

    public async IAsyncEnumerable<FileHydrationStatusResult> Handle(
        ResolveFileHydrationStatusesCommand query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cloudFiles = query.Files
            .Where(file => file.ExistsInCloud && !string.IsNullOrWhiteSpace(file.ContentHash))
            .ToList();

        if (cloudFiles.Count == 0)
        {
            yield break;
        }

        var indexEntries = await _index.LookupAsync(
            cloudFiles.Select(file => file.ContentHash!).Distinct(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        var statusByChunkHash = new Dictionary<string, FileHydrationStatus>(StringComparer.Ordinal);

        foreach (var file in cloudFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexEntries.TryGetValue(file.ContentHash!, out var entry))
            {
                _logger.LogWarning("Content hash not found in chunk index while resolving hydration status: {ContentHash}", file.ContentHash);
                yield return new FileHydrationStatusResult(file.RelativePath, file.ContentHash, FileHydrationStatus.Unknown);
                continue;
            }

            if (!statusByChunkHash.TryGetValue(entry.ChunkHash, out var status))
            {
                status = await FileHydrationStatusResolver.ResolveAsync(_blobs, entry.ChunkHash, cancellationToken).ConfigureAwait(false);
                statusByChunkHash[entry.ChunkHash] = status;
            }

            yield return new FileHydrationStatusResult(file.RelativePath, file.ContentHash, status);
        }
    }
}
