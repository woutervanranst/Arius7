using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Pipeline.Fakes;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

public class FaultingBlobServiceTests
{
    [Test]
    public async Task UploadAsync_Throws_On_First_Faultable_Upload_After_ThrowAfterN_Successes()
    {
        var inner = Substitute.For<IBlobContainerService>();
        var sut = new FaultingBlobService(inner, throwAfterN: 1);

        await sut.UploadAsync(RelativePath.Root / "chunks" / "one", new MemoryStream([1]), new Dictionary<string, string>(), BlobTier.Hot);

        await Should.ThrowAsync<IOException>(async () =>
            await sut.UploadAsync(RelativePath.Root / "chunks" / "two", new MemoryStream([2]), new Dictionary<string, string>(), BlobTier.Hot));
    }

    [Test]
    public async Task OpenWriteAsync_DoesNotCount_As_A_Completed_Upload()
    {
        var inner = Substitute.For<IBlobContainerService>();
        inner.OpenWriteAsync(RelativePath.Root / "chunks" / "one", null, default).Returns(Task.FromResult<Stream>(new MemoryStream()));
        inner.SetMetadataAsync(RelativePath.Root / "chunks" / "one", Arg.Any<IReadOnlyDictionary<string, string>>(), default).Returns(Task.CompletedTask);
        var sut = new FaultingBlobService(inner, throwAfterN: 1);

        await using var stream = await sut.OpenWriteAsync(RelativePath.Root / "chunks" / "one");

        await sut.SetMetadataAsync(RelativePath.Root / "chunks" / "one", new Dictionary<string, string>());
    }
}
