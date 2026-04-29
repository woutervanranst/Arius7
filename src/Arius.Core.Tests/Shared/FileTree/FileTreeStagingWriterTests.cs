using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    [Test]
    public void GetDirectoryId_UsesCanonicalForwardSlashPath()
    {
        var id1 = FileTreeStagingPaths.GetDirectoryId("photos/2024");
        var id2 = FileTreeStagingPaths.GetDirectoryId("photos\\2024");

        id1.ShouldBe(id2);
        id1.Length.ShouldBe(64);
        id1.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Test]
    [Arguments("/photos")]
    [Arguments("/photos/2024")]
    [Arguments("C:/photos")]
    [Arguments("C:\\photos")]
    public void GetDirectoryId_RootedPath_Throws(string directoryPath)
    {
        Should.Throw<ArgumentException>(() => FileTreeStagingPaths.GetDirectoryId(directoryPath));
    }

    [Test]
    public void GetNodeDirectory_UsesTwoCharacterFanout()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "arius-staging-test");
        var dirId = FileTreeStagingPaths.GetDirectoryId("docs");

        var nodePath = FileTreeStagingPaths.GetNodeDirectory(stagingRoot, dirId);

        nodePath.ShouldBe(Path.Combine(stagingRoot, "dirs", dirId[..2], dirId));
    }

    [Test]
    public async Task OpenAsync_DeletesExistingStagingDirectory()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            var stagingRoot = FileTreeStagingPaths.GetStagingRoot(cacheDir);
            Directory.CreateDirectory(stagingRoot);
            await File.WriteAllTextAsync(Path.Combine(stagingRoot, "stale"), "old");

            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);

            File.Exists(Path.Combine(stagingRoot, "stale")).ShouldBeFalse();
            Directory.Exists(session.StagingRoot).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task OpenAsync_FailsWhenAnotherSessionHoldsLock()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var first = await FileTreeStagingSession.OpenAsync(cacheDir);

            await Assert.ThrowsAsync<IOException>(async () =>
                await FileTreeStagingSession.OpenAsync(cacheDir));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileAsync_WritesFileEntryToParentDirectoryNode()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            var writer = new FileTreeStagingWriter(session.StagingRoot);
            var now = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
            var hash = ContentHash.Parse(new string('a', 64));

            await writer.AppendFileAsync("photos/a.jpg", hash, now, now);

            var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
            var entriesPath = FileTreeStagingPaths.GetEntriesPath(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(entriesPath)).Single();
            var entry = FileTreeBlobSerializer.ParseFileEntryLine(line);

            entry.Name.ShouldBe("a.jpg");
            entry.ContentHash.ShouldBe(hash);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileAsync_WritesChildLinksForNestedPath()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            var writer = new FileTreeStagingWriter(session.StagingRoot);
            var now = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);

            await writer.AppendFileAsync("photos/2024/a.jpg", ContentHash.Parse(new string('b', 64)), now, now);

            var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
            var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
            var photos2024Id = FileTreeStagingPaths.GetDirectoryId("photos/2024");

            var rootChildren = await File.ReadAllTextAsync(FileTreeStagingPaths.GetChildrenPath(session.StagingRoot, rootId));
            var photosChildren = await File.ReadAllTextAsync(FileTreeStagingPaths.GetChildrenPath(session.StagingRoot, photosId));

            rootChildren.ShouldContain($"{photosId} D photos/");
            photosChildren.ShouldContain($"{photos2024Id} D 2024/");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
