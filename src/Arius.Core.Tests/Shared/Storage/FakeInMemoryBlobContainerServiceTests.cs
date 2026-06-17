using Arius.Core.Tests.Shared.Storage.Fakes;
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
    public async Task ListAsync_RawNamePrefix_PassthroughDecorator_MatchesNativeFakeSemantics()
    {
        // Verifies that a pass-through decorator forwards BlobNamePrefix listing semantics to the inner
        // service unchanged, so it behaves identically to the native fake against the same seed data.
        var blobs = new FakeInMemoryBlobContainerService();
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa"), [1]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/aa0"), [2]);
        blobs.SeedBlob(RelativePath.Parse("chunk-index/ab"), [3]);
        var passthrough = new PassthroughBlobContainerService(blobs);

        var items = await ((IBlobContainerService)passthrough).ListAsync(BlobPaths.ChunkIndexPrefix / PathSegment.Parse("aa"), BlobListPrefixKind.BlobNamePrefix).ToListAsync();

        items.Select(i => i.Name.ToString()).ShouldBe(["chunk-index/aa", "chunk-index/aa0"]);
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