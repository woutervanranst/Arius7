using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly ContentHash TestHash = ContentHash.Parse(new string('a', 64));

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
    public async Task DisposeAsync_RemovesStagingRootBeforeReleasingLock()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            var first = await FileTreeStagingSession.OpenAsync(cacheDir);
            await File.WriteAllTextAsync(Path.Combine(first.StagingRoot, "owned-by-first"), "first");

            await first.DisposeAsync();

            await using var second = await FileTreeStagingSession.OpenAsync(cacheDir);
            File.Exists(Path.Combine(second.StagingRoot, "owned-by-first")).ShouldBeFalse();
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

            await writer.AppendFileAsync("photos/a.jpg", TestHash, TestTimestamp, TestTimestamp);

            var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
            var entriesPath = FileTreeStagingPaths.GetEntriesPath(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(entriesPath)).Single();
            var entry = FileTreeBlobSerializer.ParseFileEntryLine(line);

            entry.Name.ShouldBe("a.jpg");
            entry.ContentHash.ShouldBe(TestHash);
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

            await writer.AppendFileAsync("photos/2024/a.jpg", ContentHash.Parse(new string('b', 64)), TestTimestamp, TestTimestamp);

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

    [Test]
    [Arguments("/photos/a.jpg")]
    [Arguments("C:/photos/a.jpg")]
    [Arguments("C:\\photos\\a.jpg")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments(" photos/a.jpg")]
    [Arguments("photos /a.jpg")]
    [Arguments("photos/a.jpg ")]
    [Arguments("photos\\a.jpg")]
    public async Task AppendFileAsync_NonCanonicalPath_Throws(string filePath)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            var writer = new FileTreeStagingWriter(session.StagingRoot);

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await writer.AppendFileAsync(filePath, TestHash, TestTimestamp, TestTimestamp));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
