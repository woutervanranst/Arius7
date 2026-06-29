using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileTree;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Features.ArchiveCommand;

/// <summary>
/// Pins the opt-in pointer behavior: archiving a binary-present file writes NO <c>.pointer.arius</c>
/// sidecar by default; <c>WritePointers</c> re-enables it; and <c>RemoveLocal</c> implies a pointer
/// is written even when <c>WritePointers</c> is left off (the pointer becomes the sole local record).
/// </summary>
public class ArchivePointerDefaultTests
{
    [Test]
    public async Task Archive_Default_DoesNotWritePointerAndKeepsBinary()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        await WriteRandomFileAsync(fixture, relativePath, 128);

        var result = await ArchiveAsync(fixture, BlobTier.Cool); // WritePointers defaults to false

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeFalse();
        fixture.LocalFileSystem.FileExists(relativePath).ShouldBeTrue();
    }

    [Test]
    public async Task Archive_WritePointers_WritesPointer()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        await WriteRandomFileAsync(fixture, relativePath, 128);

        var result = await ArchiveAsync(fixture, BlobTier.Cool, writePointers: true);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue();
        fixture.LocalFileSystem.FileExists(relativePath).ShouldBeTrue();
    }

    [Test]
    public async Task Archive_RemoveLocal_ImpliesPointerAndRemovesBinary()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        await WriteRandomFileAsync(fixture, relativePath, 128);

        // WritePointers left default (false): --remove-local implies a pointer is written.
        var result = await ArchiveAsync(fixture, BlobTier.Cool, removeLocal: true, writePointers: false);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.LocalFileSystem.FileExists(relativePath).ShouldBeFalse();
        fixture.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue();
    }

    private static async ValueTask<RepositoryTestFixture> CreateArchiveFixtureAsync()
        => await RepositoryTestFixture.CreateWithEncryptionAsync(
            new FakeInMemoryBlobContainerService(),
            "test-account",
            $"test-container-{Guid.NewGuid():N}",
            IEncryptionService.PlaintextInstance,
            TestTempRoots.CreateDirectory("archive-pointer-default-test"));

    private static async Task WriteRandomFileAsync(RepositoryTestFixture fixture, RelativePath relativePath, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);
    }

    private static async Task<ArchiveResult> ArchiveAsync(
        RepositoryTestFixture fixture,
        BlobTier uploadTier,
        bool removeLocal = false,
        bool writePointers = false,
        CancellationToken cancellationToken = default)
    {
        var handler = fixture.CreateArchiveHandler(OpenStagingSessionAsync);

        return await handler.Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = uploadTier,
                SmallFileThreshold = 1024 * 1024,
                RemoveLocal        = removeLocal,
                WritePointers      = writePointers,
            }),
            cancellationToken);

        static async Task<IFileTreeStagingSession> OpenStagingSessionAsync(LocalDirectory path, CancellationToken ct)
            => await FileTreeStagingSession.OpenAsync(path, ct);
    }
}
