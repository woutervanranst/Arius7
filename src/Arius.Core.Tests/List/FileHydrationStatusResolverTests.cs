using Arius.Core.Features.Hydration;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Core.Tests.List;

public class FileHydrationStatusResolverTests
{
    [Test]
    public async Task ResolveAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

    [Test]
    public async Task ResolveAsync_ReturnsAvailable_WhenArchiveChunkHasCompletedRehydratedCopy()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Cool };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc", "chunks-rehydrated/abc"]);
    }

    [Test]
    public async Task ResolveAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task ResolveAsync_ReturnsPending_WhenRehydratedCopyExistsButStillArchive()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task ResolveAsync_ReturnsNeedsRehydration_WhenArchiveChunkHasNoRehydratedCopy()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.NeedsRehydration);
    }

    [Test]
    public async Task ResolveAsync_ReturnsUnknown_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = false };

        var status = await FileHydrationStatusResolver.ResolveAsync(blobs, "abc", CancellationToken.None);

        status.ShouldBe(FileHydrationStatus.Unknown);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

    private sealed class FakeBlobContainerService : IBlobContainerService
    {
        public Dictionary<string, BlobMetadata> Metadata { get; } = new(StringComparer.Ordinal);
        public List<string> RequestedBlobNames { get; } = [];

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
        {
            RequestedBlobNames.Add(blobName);
            return Task.FromResult(Metadata.TryGetValue(blobName, out var metadata) ? metadata : new BlobMetadata { Exists = false });
        }

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }
}
