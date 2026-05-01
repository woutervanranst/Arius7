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
    public void GetNodePath_UsesFlatHashFilePath()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "arius-staging-test");
        var dirId = FileTreeStagingPaths.GetDirectoryId("docs");

        var nodePath = FileTreeStagingPaths.GetNodePath(stagingRoot, dirId);

        nodePath.ShouldBe(Path.Combine(stagingRoot, dirId));
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
    public async Task AppendFileEntryAsync_WritesSingleNodeFilePerDirectoryId()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync("photos/a.jpg", TestHash, TestTimestamp, TestTimestamp);

            var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
            var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
            var rootPath = FileTreeStagingPaths.GetNodePath(session.StagingRoot, rootId);
            var photosPath = FileTreeStagingPaths.GetNodePath(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(photosPath)).Single();
            var entry = FileTreeSerializer.ParseFileEntryLine(line);

            entry.Name.ShouldBe("a.jpg");
            entry.ContentHash.ShouldBe(TestHash);
            File.Exists(rootPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_WritesDirectoryEntriesForNestedPath()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync("photos/2024/a.jpg", ContentHash.Parse(new string('b', 64)), TestTimestamp, TestTimestamp);

            var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
            var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
            var photos2024Id = FileTreeStagingPaths.GetDirectoryId("photos/2024");

            var rootEntries = await File.ReadAllLinesAsync(FileTreeStagingPaths.GetNodePath(session.StagingRoot, rootId));
            var photosEntries = await File.ReadAllLinesAsync(FileTreeStagingPaths.GetNodePath(session.StagingRoot, photosId));

            rootEntries.ShouldContain($"{photosId} D photos/");
            photosEntries.ShouldContain($"{photos2024Id} D 2024/");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_PreservesLeadingAndTrailingSpacesInPathSegments()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(" photos/a.jpg ", TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
            var spacedDirectoryId = FileTreeStagingPaths.GetDirectoryId(" photos");
            var rootEntries = await File.ReadAllLinesAsync(FileTreeStagingPaths.GetNodePath(session.StagingRoot, rootId));
            var nodePath = FileTreeStagingPaths.GetNodePath(session.StagingRoot, spacedDirectoryId);
            var line = (await File.ReadAllLinesAsync(nodePath)).Single(l => l.Contains(" F ", StringComparison.Ordinal));
            var entry = FileTreeSerializer.ParseFileEntryLine(line);

            rootEntries.ShouldContain($"{spacedDirectoryId} D  photos/");
            entry.Name.ShouldBe("a.jpg ");
            entry.ContentHash.ShouldBe(TestHash);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    [Arguments(" /a.jpg")]
    [Arguments("photos/   /a.jpg")]
    public async Task AppendFileEntryAsync_WhitespaceOnlyDirectorySegment_Throws(string filePath)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await writer.AppendFileEntryAsync(filePath, TestHash, TestTimestamp, TestTimestamp));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    [Arguments("dir/ ")]
    [Arguments(" ")]
    public async Task AppendFileEntryAsync_WhitespaceOnlyFileNameSegment_Throws(string filePath)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await writer.AppendFileEntryAsync(filePath, TestHash, TestTimestamp, TestTimestamp));
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
    [Arguments("photos\\a.jpg")]
    public async Task AppendFileEntryAsync_NonCanonicalPath_Throws(string filePath)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await writer.AppendFileEntryAsync(filePath, TestHash, TestTimestamp, TestTimestamp));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_UsesBoundedLockStripeCount()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            for (var i = 0; i < 2_000; i++)
                await writer.AppendFileEntryAsync($"dir-{i}/file-{i}.bin", TestHash, TestTimestamp, TestTimestamp);

            writer.LockStripeCount.ShouldBe(writer.ActiveLockCount);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
