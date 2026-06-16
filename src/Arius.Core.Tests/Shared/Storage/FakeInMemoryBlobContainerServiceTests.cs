using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.Storage;

public class FakeInMemoryBlobContainerServiceTests
{
    [Test]
    public async Task DownloadAsync_MissingBlob_ThrowsBlobNotFoundException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var blobName = RelativePath.Parse("chunks/missing-fake-download");

        var ex = await Should.ThrowAsync<BlobNotFoundException>(() => blobs.DownloadAsync(blobName));

        ex.BlobName.ShouldBe(blobName);
    }

    [Test]
    public async Task TryDownloadAsync_MissingBlob_ReturnsNull()
    {
        var blobs = new FakeInMemoryBlobContainerService();

        var stream = await blobs.TryDownloadAsync(RelativePath.Parse("chunks/missing-fake-try-download"));

        stream.ShouldBeNull();
    }

    [Test]
    public async Task ListAsync_RawNamePrefix_MatchesNonSegmentAligned()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa"), [1]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa0"), [2]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa3f"), [3]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/ab"), [4]);

        var items = await blobs.ListAsync(BlobPaths.ChunkIndexPrefix / PathSegment.Parse("aa"), BlobListPrefixKind.BlobNamePrefix).ToListAsync();

        items.Select(i => i.Name.ToString()).ShouldBe(["chunk-index/aa", "chunk-index/aa0", "chunk-index/aa3f"]);
        items.ShouldAllBe(i => i.ETag != null);
        blobs.ListedNamePrefixes.ShouldBe(["chunk-index/aa"]);
        blobs.RequestedBlobNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ListAsync_RawNamePrefix_DefaultInterfaceImplementation_MatchesNativeFakeSemantics()
    {
        // Exercises the IBlobContainerService DEFAULT implementation (directory listing + client-side
        // filter) against the same seed data, so decorators that do not override the overload behave
        // identically to native implementations.
        var blobs = new FakeInMemoryBlobContainerService();
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa"), [1]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa0"), [2]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/ab"), [3]);
        var passthrough = new PassthroughBlobContainerService(blobs);

        var items = await ((IBlobContainerService)passthrough).ListAsync(BlobPaths.ChunkIndexPrefix / PathSegment.Parse("aa"), BlobListPrefixKind.BlobNamePrefix).ToListAsync();

        items.Select(i => i.Name.ToString()).ShouldBe(["chunk-index/aa", "chunk-index/aa0"]);
    }

    /// <summary>Minimal decorator without an override for the raw name-prefix overload.</summary>
    private sealed class PassthroughBlobContainerService(IBlobContainerService inner) : IBlobContainerService
    {
        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => inner.CreateContainerIfNotExistsAsync(cancellationToken);
        public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);
        public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => inner.OpenWriteAsync(blobName, contentType, cancellationToken);
        public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => inner.DownloadAsync(blobName, cancellationToken);
        public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => inner.TryDownloadAsync(blobName, cancellationToken);
        public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) => inner.GetMetadataAsync(blobName, cancellationToken);
        public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, includeMetadata, cancellationToken);
        public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => inner.SetMetadataAsync(blobName, metadata, cancellationToken);
        public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => inner.SetTierAsync(blobName, tier, cancellationToken);
        public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);
        public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => inner.DeleteAsync(blobName, cancellationToken);
    }

    [Test]
    public async Task TryDownloadAsync_ExistingBlob_ReturnsContent()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var blobName = RelativePath.Parse("chunks/existing-fake-try-download");
        var content = "fake try-download"u8.ToArray();
        blobs.SeedBlob(blobName, content);

        var download = await blobs.TryDownloadAsync(blobName);
        download.ShouldNotBeNull();
        await using var stream = download.Stream;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(content);
        download.ETag.ShouldStartWith("fake:");
    }
}
