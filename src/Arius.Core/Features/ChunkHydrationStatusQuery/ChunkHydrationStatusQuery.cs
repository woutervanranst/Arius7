using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Arius.Core.Features.ChunkHydrationStatusQuery;

public sealed record ChunkHydrationStatusQuery(IReadOnlyList<RepositoryFileEntry> Files) : IStreamQuery<ChunkHydrationStatusResult>;

public sealed record ChunkHydrationStatusResult(string RelativePath, string? ContentHash, ChunkHydrationStatus Status);


public sealed class ChunkHydrationStatusQueryHandler : IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>
{
    private readonly IBlobContainerService _blobs;
    private readonly ChunkIndexService _index;
    private readonly IChunkStorageService _chunkStorage;
    private readonly ILogger<ChunkHydrationStatusQueryHandler> _logger;

    public ChunkHydrationStatusQueryHandler(
        IBlobContainerService blobs,
        ChunkIndexService index,
        IChunkStorageService chunkStorage,
        ILogger<ChunkHydrationStatusQueryHandler> logger)
    {
        _blobs = blobs;
        _index = index;
        _chunkStorage = chunkStorage;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChunkHydrationStatusResult> Handle(
        ChunkHydrationStatusQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

        var statusByChunkHash = new Dictionary<string, ChunkHydrationStatus>(StringComparer.Ordinal);

        foreach (var file in cloudFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexEntries.TryGetValue(file.ContentHash!, out var entry))
            {
                _logger.LogWarning("Content hash not found in chunk index while resolving hydration status: {ContentHash}", file.ContentHash);
                yield return new ChunkHydrationStatusResult(file.RelativePath, file.ContentHash, ChunkHydrationStatus.Unknown);
                continue;
            }

            if (!statusByChunkHash.TryGetValue(entry.ChunkHash, out var status))
            {
                status = await _chunkStorage.GetHydrationStatusAsync(entry.ChunkHash, cancellationToken).ConfigureAwait(false);
                statusByChunkHash[entry.ChunkHash] = status;
            }

            yield return new ChunkHydrationStatusResult(file.RelativePath, file.ContentHash, status);
        }
    }
}
