using Arius.Core.Shared.FileTree;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingSessionTests
{
    private static async Task WithCacheDirectoryAsync(Func<LocalDirectory, RelativeFileSystem, Task> testBody)
    {
        var cacheDir = TestTempRoots.CreateDirectory("cache");
        var cacheFileSystem = new RelativeFileSystem(cacheDir);

        try
        {
            await testBody(cacheDir, cacheFileSystem);
        }
        finally
        {
            cacheFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task OpenAsync_DeletesExistingStagingDirectory()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            var stagingRoot = FileTreePaths.GetStagingRootDirectory(cacheDir);
            var stagingFileSystem = new RelativeFileSystem(stagingRoot);
            stagingFileSystem.CreateDirectory(RelativePath.Root);
            await File.WriteAllTextAsync(stagingRoot.Resolve(RelativePath.Parse("stale")), "old");

            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);

            stagingFileSystem.FileExists(RelativePath.Parse("stale")).ShouldBeFalse();
            stagingFileSystem.DirectoryExists(session.StagingRoot).ShouldBeTrue();
        });
    }

    [Test]
    public async Task OpenAsync_FailsWhenAnotherSessionHoldsLock()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var first = await FileTreeStagingSession.OpenAsync(cacheDir);

            await Assert.ThrowsAsync<IOException>(async () =>
                await FileTreeStagingSession.OpenAsync(cacheDir));
        });
    }

    [Test]
    public async Task DisposeAsync_RemovesStagingRootBeforeReleasingLock()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            var first = await FileTreeStagingSession.OpenAsync(cacheDir);
            await File.WriteAllTextAsync(Path.Combine(first.StagingRoot.ToString(), "owned-by-first"), "first");

            await first.DisposeAsync();

            await using var second = await FileTreeStagingSession.OpenAsync(cacheDir);
            new RelativeFileSystem(second.StagingRoot).FileExists(RelativePath.Parse("owned-by-first")).ShouldBeFalse();
        });
    }

    [Test]
    public async Task DisposeAsync_RemovesStagingRootImmediately()
    {
        await WithCacheDirectoryAsync(async (cacheDir, cacheFileSystem) =>
        {
            var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            await File.WriteAllTextAsync(Path.Combine(session.StagingRoot.ToString(), "owned-by-first"), "first");

            await session.DisposeAsync();

            cacheFileSystem.DirectoryExists(FileTreePaths.GetStagingRootDirectory(cacheDir)).ShouldBeFalse();
        });
    }
}
