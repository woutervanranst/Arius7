using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Tests.Fakes;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Core.Tests.List;

public class FileHydrationStatusResolverTests
{
    [Test]
    public async Task ResolveAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

    [Test]
    public async Task ResolveAsync_ReturnsAvailable_WhenArchiveChunkHasCompletedRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Cool };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc", "chunks-rehydrated/abc"]);
    }

    [Test]
    public async Task ResolveAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task ResolveAsync_ReturnsPending_WhenRehydratedCopyExistsButStillArchive()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task ResolveAsync_ReturnsNeedsRehydration_WhenArchiveChunkHasNoRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.NeedsRehydration);
    }

    [Test]
    public async Task ResolveAsync_ReturnsUnknown_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = false };

        var status = await ChunkHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

}
