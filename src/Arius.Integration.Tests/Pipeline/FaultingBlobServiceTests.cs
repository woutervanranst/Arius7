using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Pipeline.Fakes;
using Arius.Tests.Shared;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

public class FaultingBlobServiceTests
{
    [Test]
    public async Task UploadAsync_Throws_On_First_Faultable_Upload_After_ThrowAfterN_Successes()
    {
        var inner = Substitute.For<IBlobContainerService>();
        var sut = new FaultingBlobService(inner, throwAfterN: 1);

        await sut.UploadAsync(BlobPaths.ChunksPrefix / "one", new MemoryStream([1]), new Dictionary<string, string>(), BlobTier.Hot);

        await Should.ThrowAsync<IOException>(async () =>
            await sut.UploadAsync(BlobPaths.ChunksPrefix / "two", new MemoryStream([2]), new Dictionary<string, string>(), BlobTier.Hot));
    }

    [Test]
    public async Task OpenWriteAsync_DoesNotCount_As_A_Completed_Upload()
    {
        var inner = Substitute.For<IBlobContainerService>();
        inner.OpenWriteAsync(BlobPaths.ChunksPrefix / "one", null, default).Returns(Task.FromResult<Stream>(new MemoryStream()));
        inner.SetMetadataAsync(BlobPaths.ChunksPrefix / "one", Arg.Any<IReadOnlyDictionary<string, string>>(), default).Returns(Task.CompletedTask);
        var sut = new FaultingBlobService(inner, throwAfterN: 1);

        await using var stream = await sut.OpenWriteAsync(BlobPaths.ChunksPrefix / "one");

        await sut.SetMetadataAsync(BlobPaths.ChunksPrefix / "one", new Dictionary<string, string>());
    }
}
