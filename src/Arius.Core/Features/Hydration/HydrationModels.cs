using Arius.Core.Features.List;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.Hydration;

public enum FileHydrationStatus
{
    Unknown,
    Available,
    NeedsRehydration,
    RehydrationPending,
}

public sealed record ResolveFileHydrationStatusesCommand(IReadOnlyList<RepositoryFileEntry> Files)
    : IStreamQuery<FileHydrationStatusResult>;

public sealed record FileHydrationStatusResult(
    string RelativePath,
    string? ContentHash,
    FileHydrationStatus Status);

public static class FileHydrationStatusResolver
{
    public static async Task<FileHydrationStatus> ResolveAsync(
        IBlobContainerService blobs,
        string chunkHash,
        CancellationToken cancellationToken)
    {
        var chunkName = BlobPaths.Chunk(chunkHash);
        var chunkMeta = await blobs.GetMetadataAsync(chunkName, cancellationToken).ConfigureAwait(false);

        if (!chunkMeta.Exists)
        {
            return FileHydrationStatus.Unknown;
        }

        if (chunkMeta.Tier != BlobTier.Archive)
        {
            return FileHydrationStatus.Available;
        }

        var rehydratedName = BlobPaths.ChunkRehydrated(chunkHash);
        var rehydratedMeta = await blobs.GetMetadataAsync(rehydratedName, cancellationToken).ConfigureAwait(false);

        if (rehydratedMeta.Exists)
        {
            return rehydratedMeta.Tier == BlobTier.Archive
                ? FileHydrationStatus.RehydrationPending
                : FileHydrationStatus.Available;
        }

        return chunkMeta.IsRehydrating
            ? FileHydrationStatus.RehydrationPending
            : FileHydrationStatus.NeedsRehydration;
    }
}
