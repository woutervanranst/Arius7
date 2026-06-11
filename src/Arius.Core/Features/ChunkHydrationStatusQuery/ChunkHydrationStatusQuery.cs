using System.Runtime.CompilerServices;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Mediator;
using Microsoft.Extensions.Logging;
using ChunkHydrationStatus = Arius.Core.Shared.ChunkStorage.ChunkHydrationStatus;

namespace Arius.Core.Features.ChunkHydrationStatusQuery;

public sealed record ChunkHydrationStatusQuery(IReadOnlyList<RepositoryFileEntry> Files) : IStreamQuery<ChunkHydrationStatusResult>;

public sealed record ChunkHydrationStatusResult(RelativePath RelativePath, ContentHash? ContentHash, ChunkHydrationStatus Status);


public sealed class ChunkHydrationStatusQueryHandler : IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>
{
    private readonly IChunkIndexService _chunkIndex;
    private readonly IChunkStorageService _chunkStorage;
    private readonly ILogger<ChunkHydrationStatusQueryHandler> _logger;

    public ChunkHydrationStatusQueryHandler(
        IChunkIndexService index,
        IChunkStorageService chunkStorage,
        ILogger<ChunkHydrationStatusQueryHandler> logger)
    {
        _chunkIndex = index;
        _chunkStorage = chunkStorage;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChunkHydrationStatusResult> Handle(
        ChunkHydrationStatusQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repositoryFiles = query.Files
            .Where(file => file.State.HasFlag(RepositoryEntryState.Repository) && file.ContentHash is not null)
            .Select(file => (File: file, ContentHash: file.ContentHash!.Value))
            .ToList();

        if (repositoryFiles.Count == 0)
        {
            yield break;
        }

        var indexEntries = await _chunkIndex.LookupAsync(
            repositoryFiles.Select(file => file.ContentHash).Distinct(),
            cancellationToken).ConfigureAwait(false);

        var statusByChunkHash = new Dictionary<ChunkHash, ChunkHydrationStatus>();

        foreach (var (file, contentHash) in repositoryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexEntries.TryGetValue(contentHash, out var entry))
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
