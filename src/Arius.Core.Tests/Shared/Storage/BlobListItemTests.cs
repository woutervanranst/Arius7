using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.Storage;

public class BlobListItemTests
{
    [Test]
    public async Task FakeInMemoryListAsync_ReturnsNamesWithoutMetadataByDefault()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var blobName = BlobPaths.ChunkPath(FakeChunkHash('a'));

        blobs.SeedBlob(
            blobName,
            [1, 2, 3],
            BlobTier.Hot,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.ParentChunkHash] = FakeChunkHash('b').ToString(),
            });

        var items = await blobs.ListAsync(BlobPaths.ChunksPrefix).ToListAsync();

        items.Count.ShouldBe(1);
        items[0].Name.ShouldBe(blobName);
        items[0].Metadata.ShouldBeNull();
        items[0].ContentLength.ShouldBeNull();
    }

    [Test]
    public async Task FakeInMemoryListAsync_WithMetadata_ReturnsMetadataAndContentLength()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var blobName = BlobPaths.ChunkPath(FakeChunkHash('a'));
        var parentChunkHash = FakeChunkHash('b').ToString();

        blobs.SeedBlob(
            blobName,
            [1, 2, 3],
            BlobTier.Hot,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.ParentChunkHash] = parentChunkHash,
            });

        var items = await blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true).ToListAsync();

        items.Count.ShouldBe(1);
        items[0].Name.ShouldBe(blobName);
        var metadata = items[0].Metadata.ShouldNotBeNull();
        metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata[BlobMetadataKeys.ParentChunkHash].ShouldBe(parentChunkHash);
        items[0].ContentLength.ShouldBe(3);
    }
}
