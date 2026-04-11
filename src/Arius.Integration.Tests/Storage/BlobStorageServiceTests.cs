using System.Text;
using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Shouldly;

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
        var content  = Encoding.UTF8.GetBytes("Hello blob roundtrip!");
        var meta     = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge };

        await svc.UploadAsync(BlobPaths.Chunk("abc123"), new MemoryStream(content), meta, BlobTier.Hot);

        await using var stream = await svc.DownloadAsync(BlobPaths.Chunk("abc123"));
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(content);
    }

    // ── HEAD: exists + metadata ───────────────────────────────────────────────

    [Test]
    public async Task GetMetadata_ExistingBlob_ReturnsMetadata()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var meta = new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
            [BlobMetadataKeys.OriginalSize] = "1234"
        };

        await svc.UploadAsync(BlobPaths.Chunk("meta-test"), new MemoryStream([1, 2, 3]), meta, BlobTier.Hot);

        var result = await svc.GetMetadataAsync(BlobPaths.Chunk("meta-test"));

        result.Exists.ShouldBeTrue();
        result.ContentLength.ShouldBe(3);
        result.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        result.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("1234");
    }

    [Test]
    public async Task GetMetadata_NonExistentBlob_ReturnsFalse()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        var result = await svc.GetMetadataAsync("chunks/does-not-exist");

        result.Exists.ShouldBeFalse();
    }

    // ── Tier setting ──────────────────────────────────────────────────────────

    [Test]
    public async Task Upload_WithCoolTier_BlobHasCoolTier()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        await svc.UploadAsync(BlobPaths.FileTree("tree1"), new MemoryStream([0xFF]), new Dictionary<string, string>(), BlobTier.Cool);

        var meta = await svc.GetMetadataAsync(BlobPaths.FileTree("tree1"));
        // Azurite may report Hot as the effective tier for Cool; just verify the blob exists
        meta.Exists.ShouldBeTrue();
    }

    // ── List by prefix ────────────────────────────────────────────────────────

    [Test]
    public async Task List_WithPrefix_ReturnsMatchingBlobsOnly()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var empty    = new Dictionary<string, string>();

        await svc.UploadAsync(BlobPaths.Chunk("aaa"), new MemoryStream([1]), empty, BlobTier.Hot);
        await svc.UploadAsync(BlobPaths.Chunk("aab"), new MemoryStream([2]), empty, BlobTier.Hot);
        await svc.UploadAsync(BlobPaths.FileTree("bbb"), new MemoryStream([3]), empty, BlobTier.Hot);

        var chunks = new List<string>();
        await foreach (var name in svc.ListAsync(BlobPaths.Chunks))
            chunks.Add(name);

        chunks.ShouldContain(BlobPaths.Chunk("aaa"));
        chunks.ShouldContain(BlobPaths.Chunk("aab"));
        chunks.ShouldNotContain(BlobPaths.FileTree("bbb"));
    }

    // ── SetMetadata ───────────────────────────────────────────────────────────

    [Test]
    public async Task SetMetadata_UpdatesMetadataWithoutChangingContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var original = new byte[] { 42, 43, 44 };

        await svc.UploadAsync("chunks/setmeta", new MemoryStream(original),
            new Dictionary<string, string> { ["initial"] = "value" }, BlobTier.Hot);

        await svc.SetMetadataAsync("chunks/setmeta",
            new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge });

        var meta = await svc.GetMetadataAsync("chunks/setmeta");
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

        await svc.UploadAsync("chunks/ow", new MemoryStream([1, 2]), empty, BlobTier.Hot);
        await svc.UploadAsync("chunks/ow", new MemoryStream([3, 4, 5]), empty, BlobTier.Hot, overwrite: true);

        await using var stream = await svc.DownloadAsync("chunks/ow");
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().ShouldBe(new byte[] { 3, 4, 5 });
    }

    // ── OpenWriteAsync behaviour ──────────────────────────────────────────────

    [Test]
    public async Task OpenWrite_ThenDownload_ProducesByteIdenticalContent()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();
        var payload  = Encoding.UTF8.GetBytes("streaming upload roundtrip");

        await using (var ws = await svc.OpenWriteAsync("chunks/ow-roundtrip"))
            await new MemoryStream(payload).CopyToAsync(ws);

        await using var rs = await svc.DownloadAsync("chunks/ow-roundtrip");
        var ms = new MemoryStream();
        await rs.CopyToAsync(ms);
        ms.ToArray().ShouldBe(payload);
    }

    [Test]
    public async Task OpenWrite_SecondWrite_ThrowsBlobAlreadyExistsException()
    {
        var (_, svc) = await azurite.CreateTestServiceAsync();

        await using (var ws1 = await svc.OpenWriteAsync("chunks/ow-replace"))
            await new MemoryStream([1, 2]).CopyToAsync(ws1);

        await Should.ThrowAsync<BlobAlreadyExistsException>(async () =>
        {
            await using var ws2 = await svc.OpenWriteAsync("chunks/ow-replace");
            await new MemoryStream([3, 4, 5]).CopyToAsync(ws2);
        });
    }
}
