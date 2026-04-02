using Arius.Core.Shared.Storage;

namespace Arius.Core.Features.ChunkHydrationStatusQuery;

internal static class ChunkHydrationStatusResolver
{
    public static async Task<ChunkHydrationStatus> ResolveAsync(
        IBlobContainerService blobs,
        string chunkHash,
        CancellationToken cancellationToken)
    {
        var chunkName = BlobPaths.Chunk(chunkHash);
        var chunkMeta = await blobs.GetMetadataAsync(chunkName, cancellationToken).ConfigureAwait(false);

        if (!chunkMeta.Exists)
        {
            return ChunkHydrationStatus.Unknown;
        }

        if (chunkMeta.Tier != BlobTier.Archive)
        {
            return ChunkHydrationStatus.Available;
        }

        var rehydratedName = BlobPaths.ChunkRehydrated(chunkHash);
        var rehydratedMeta = await blobs.GetMetadataAsync(rehydratedName, cancellationToken).ConfigureAwait(false);

        if (rehydratedMeta.Exists)
        {
            return rehydratedMeta.Tier == BlobTier.Archive
                ? ChunkHydrationStatus.RehydrationPending
                : ChunkHydrationStatus.Available;
        }

        return chunkMeta.IsRehydrating
            ? ChunkHydrationStatus.RehydrationPending
            : ChunkHydrationStatus.NeedsRehydration;
    }
}