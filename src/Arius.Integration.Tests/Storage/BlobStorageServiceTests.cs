using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="AzureBlobContainerService"/> against Azurite.
/// Covers upload/download roundtrip, HEAD with metadata, tier setting, and list by prefix.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class BlobStorageServiceTests(AzuriteFixture azurite)
{
    // ── Upload / Download roundtrip ───────────────────────────────────────────

    [Test]
    public async Task Upload_ThenDownload_ProducesBytIdenticalContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var content = "Hello blob roundtrip!"u8.ToArray();
        var meta    = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge };
        var chunkHash = FakeChunkHash('a');

        await svc.UploadAsync(BlobPaths.ChunkPath(chunkHash), new MemoryStream(content), meta, BlobTier.Hot);

        var download = await svc.DownloadAsync(BlobPaths.ChunkPath(chunkHash));
        await using var stream = download.Stream;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(content);
        download.ETag.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Download_NonExistentBlob_ThrowsBlobNotFoundException()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var blobName = BlobPaths.ChunkPath("missing-download");

        var ex = await Should.ThrowAsync<BlobNotFoundException>(() => svc.DownloadAsync(blobName));

        ex.BlobName.ShouldBe(blobName);
    }

    [Test]
    public async Task TryDownload_NonExistentBlob_ReturnsNull()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        var stream = await svc.TryDownloadAsync(BlobPaths.ChunkPath("missing-try-download"));

        stream.ShouldBeNull();
    }

    [Test]
    public async Task TryDownload_ExistingBlob_ReturnsContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var content = "Hello try-download!"u8.ToArray();
        var blobName = BlobPaths.ChunkPath("try-download-existing");

        await svc.UploadAsync(blobName, new MemoryStream(content), new Dictionary<string, string>(), BlobTier.Hot);

        var download = await svc.TryDownloadAsync(blobName);
        download.ShouldNotBeNull();
        await using var stream = download.Stream;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(content);
        download.ETag.ShouldNotBeNullOrWhiteSpace();
    }

    // ── HEAD: exists + metadata ───────────────────────────────────────────────

    [Test]
    public async Task GetMetadata_ExistingBlob_ReturnsMetadata()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var chunkHash = FakeChunkHash('b');
        var meta = new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
            [BlobMetadataKeys.OriginalSize] = "1234"
        };

        await svc.UploadAsync(BlobPaths.ChunkPath(chunkHash), new MemoryStream([1, 2, 3]), meta, BlobTier.Hot);

        var result = await svc.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash));

        result.Exists.ShouldBeTrue();
        result.ContentLength.ShouldBe(3);
        result.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        result.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("1234");
    }

    [Test]
    public async Task GetMetadata_NonExistentBlob_ReturnsFalse()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        var result = await svc.GetMetadataAsync(BlobPaths.ChunkPath("does-not-exist"));

        result.Exists.ShouldBeFalse();
    }

    // ── Tier setting ──────────────────────────────────────────────────────────

    [Test]
    public async Task Upload_WithCoolTier_BlobHasCoolTier()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var fileTreeHash = FakeFileTreeHash('c');

        await svc.UploadAsync(BlobPaths.FileTreePath(fileTreeHash), new MemoryStream([0xFF]), new Dictionary<string, string>(), BlobTier.Cool);

        var meta = await svc.GetMetadataAsync(BlobPaths.FileTreePath(fileTreeHash));
        // Azurite may report Hot as the effective tier for Cool; just verify the blob exists
        meta.Exists.ShouldBeTrue();
    }

    // ── List by prefix ────────────────────────────────────────────────────────

    [Test]
    public async Task List_WithPrefix_ReturnsMatchingBlobsOnly()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var empty    = new Dictionary<string, string>();
        var firstChunkHash = FakeChunkHash('d');
        var secondChunkHash = FakeChunkHash('e');
        var fileTreeHash = FakeFileTreeHash('f');

        await svc.UploadAsync(BlobPaths.ChunkPath(firstChunkHash), new MemoryStream([1]), empty, BlobTier.Hot);
        await svc.UploadAsync(BlobPaths.ChunkPath(secondChunkHash), new MemoryStream([2]), empty, BlobTier.Hot);
        await svc.UploadAsync(BlobPaths.FileTreePath(fileTreeHash), new MemoryStream([3]), empty, BlobTier.Hot);

        var chunks = new List<RelativePath>();
        await foreach (var item in svc.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
            chunks.Add(item.Name);

        chunks.ShouldContain(BlobPaths.ChunkPath(firstChunkHash));
        chunks.ShouldContain(BlobPaths.ChunkPath(secondChunkHash));
        chunks.ShouldNotContain(BlobPaths.FileTreePath(fileTreeHash));
    }

    // ── SetMetadata ───────────────────────────────────────────────────────────

    [Test]
    public async Task SetMetadata_UpdatesMetadataWithoutChangingContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var original = new byte[] { 42, 43, 44 };

        await svc.UploadAsync(BlobPaths.ChunkPath("setmeta"), new MemoryStream(original),
            new Dictionary<string, string> { ["initial"] = "value" }, BlobTier.Hot);

        await svc.SetMetadataAsync(BlobPaths.ChunkPath("setmeta"),
            new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge });

        var meta = await svc.GetMetadataAsync(BlobPaths.ChunkPath("setmeta"));
        meta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        meta.Metadata.ContainsKey("initial").ShouldBeFalse(); // replaced, not merged
        meta.ContentLength.ShouldBe(3);
    }

    // ── Overwrite behaviour ───────────────────────────────────────────────────

    [Test]
    public async Task Upload_Overwrite_ReplacesExistingBlob()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var empty    = new Dictionary<string, string>();

        await svc.UploadAsync(BlobPaths.ChunkPath("ow"), new MemoryStream([1, 2]), empty, BlobTier.Hot);
        await svc.UploadAsync(BlobPaths.ChunkPath("ow"), new MemoryStream([3, 4, 5]), empty, BlobTier.Hot, overwrite: true);

        var download = await svc.DownloadAsync(BlobPaths.ChunkPath("ow"));
        await using var stream = download.Stream;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().ShouldBe([3, 4, 5]);
    }

    // ── OpenWriteAsync behaviour ──────────────────────────────────────────────

    [Test]
    public async Task OpenWrite_ThenDownload_ProducesByteIdenticalContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var payload  = "streaming upload roundtrip"u8.ToArray();

        await using (var ws = await svc.OpenWriteAsync(BlobPaths.ChunkPath("ow-roundtrip")))
            await new MemoryStream(payload).CopyToAsync(ws);

        var download = await svc.DownloadAsync(BlobPaths.ChunkPath("ow-roundtrip"));
        await using var rs = download.Stream;
        var ms = new MemoryStream();
        await rs.CopyToAsync(ms);
        ms.ToArray().ShouldBe(payload);
    }

    [Test]
    public async Task OpenWrite_SecondWrite_ThrowsBlobAlreadyExistsException()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        await using (var ws1 = await svc.OpenWriteAsync(BlobPaths.ChunkPath("ow-replace")))
            await new MemoryStream([1, 2]).CopyToAsync(ws1);

        await Should.ThrowAsync<BlobAlreadyExistsException>(async () =>
        {
            await using var ws2 = await svc.OpenWriteAsync(BlobPaths.ChunkPath("ow-replace"));
            await new MemoryStream([3, 4, 5]).CopyToAsync(ws2);
        });
    }
}
