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
    public async Task TryDownloadAsync_ExistingBlob_ReturnsContent()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var blobName = RelativePath.Parse("chunks/existing-fake-try-download");
        var content = "fake try-download"u8.ToArray();
        blobs.SeedBlob(blobName, content);

        await using var stream = await blobs.TryDownloadAsync(blobName);
        stream.ShouldNotBeNull();
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(content);
    }
}
