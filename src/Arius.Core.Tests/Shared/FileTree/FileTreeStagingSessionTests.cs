using Arius.Core.Shared.FileTree;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingSessionTests : IDisposable
{
    private readonly LocalDirectory _cacheDir;
    private readonly RelativeFileSystem _cacheFileSystem;

    public FileTreeStagingSessionTests()
    {
        _cacheDir = TestTempRoots.CreateDirectory("cache");
        _cacheFileSystem = new RelativeFileSystem(_cacheDir);
    }

    public void Dispose() => _cacheFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);

    [Test]
    public async Task OpenAsync_DeletesExistingStagingDirectory()
    {
        var stagingRoot = FileTreePaths.GetStagingRootDirectory(_cacheDir);
        var stagingFileSystem = new RelativeFileSystem(stagingRoot);
        stagingFileSystem.CreateDirectory(RelativePath.Root);
        await File.WriteAllTextAsync(stagingRoot.Resolve(RelativePath.Parse("stale")), "old");

        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);

        stagingFileSystem.FileExists(RelativePath.Parse("stale")).ShouldBeFalse();
        stagingFileSystem.DirectoryExists(session.StagingRoot).ShouldBeTrue();
    }

    [Test]
    public async Task OpenAsync_FailsWhenAnotherSessionHoldsLock()
    {
        await using var first = await FileTreeStagingSession.OpenAsync(_cacheDir);

        await Assert.ThrowsAsync<IOException>(async () =>
            await FileTreeStagingSession.OpenAsync(_cacheDir));
    }

    [Test]
    public async Task DisposeAsync_RemovesStagingRootBeforeReleasingLock()
    {
        var first = await FileTreeStagingSession.OpenAsync(_cacheDir);
        await File.WriteAllTextAsync(Path.Combine(first.StagingRoot.ToString(), "owned-by-first"), "first");

        await first.DisposeAsync();

        await using var second = await FileTreeStagingSession.OpenAsync(_cacheDir);
        new RelativeFileSystem(second.StagingRoot).FileExists(RelativePath.Parse("owned-by-first")).ShouldBeFalse();
    }

    [Test]
    public async Task DisposeAsync_RemovesStagingRootImmediately()
    {
        var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        await File.WriteAllTextAsync(Path.Combine(session.StagingRoot.ToString(), "owned-by-first"), "first");

        await session.DisposeAsync();

        _cacheFileSystem.DirectoryExists(FileTreePaths.GetStagingRootDirectory(_cacheDir)).ShouldBeFalse();
    }
}
