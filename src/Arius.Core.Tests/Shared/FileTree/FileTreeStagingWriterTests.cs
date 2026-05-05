using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly ContentHash TestHash = ContentHash.Parse(new string('a', 64));

    [Test]
    [Arguments("/photos")]
    [Arguments("/photos/2024")]
    [Arguments("C:/photos")]
    [Arguments("C:\\photos")]
    public void GetDirectoryId_RootedPath_Throws(string directoryPath)
    {
        Should.Throw<ArgumentException>(() => FileTreePaths.GetStagingDirectoryId(RelativePath.Parse(directoryPath)));
    }

    [Test]
    [Arguments("photos//2024")]
    [Arguments("photos/./2024")]
    [Arguments("photos/../2024")]
    [Arguments("photos\\2024")]
    public void GetDirectoryId_NonCanonicalPath_Throws(string directoryPath)
    {
        Should.Throw<ArgumentException>(() => FileTreePaths.GetStagingDirectoryId(RelativePath.Parse(directoryPath)));
    }

    [Test]
    public void FileTreePaths_ExposeTypedStagingHelpers()
    {
        var stagingRootMethod = typeof(FileTreePaths).GetMethod(nameof(FileTreePaths.GetStagingRootDirectory), [typeof(LocalRootPath)]);
        var stagingNodeMethod = typeof(FileTreePaths).GetMethod(nameof(FileTreePaths.GetStagingNodePath), [typeof(LocalRootPath), typeof(string)]);
        var stagingLockMethod = typeof(FileTreePaths).GetMethod(nameof(FileTreePaths.GetStagingLockPath), [typeof(LocalRootPath)]);

        stagingRootMethod.ShouldNotBeNull();
        stagingRootMethod.ReturnType.ShouldBe(typeof(LocalRootPath));

        stagingNodeMethod.ShouldNotBeNull();
        stagingNodeMethod.ReturnType.ShouldBe(typeof(RootedPath));

        stagingLockMethod.ShouldNotBeNull();
        stagingLockMethod.ReturnType.ShouldBe(typeof(RootedPath));
    }

    [Test]
    public async Task OpenAsync_DeletesExistingStagingDirectory()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            var cacheRoot = LocalRootPath.Parse(cacheDir);
            var stagingRoot = FileTreePaths.GetStagingRootDirectory(cacheRoot);
            stagingRoot.CreateDirectory();
            await (stagingRoot / RelativePath.Parse("stale")).WriteAllTextAsync("old");

            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);

            (stagingRoot / RelativePath.Parse("stale")).ExistsFile.ShouldBeFalse();
            session.StagingRoot.ExistsDirectory.ShouldBeTrue();
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
            await (first.StagingRoot / RelativePath.Parse("owned-by-first")).WriteAllTextAsync("first");

            await first.DisposeAsync();

            await using var second = await FileTreeStagingSession.OpenAsync(cacheDir);
            (second.StagingRoot / RelativePath.Parse("owned-by-first")).ExistsFile.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task DisposeAsync_RemovesStagingRootImmediately()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            await (session.StagingRoot / RelativePath.Parse("owned-by-first")).WriteAllTextAsync("first");

            await session.DisposeAsync();

            FileTreePaths.GetStagingRootDirectory(LocalRootPath.Parse(cacheDir)).ExistsDirectory.ShouldBeFalse();
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

            await writer.AppendFileEntryAsync(RelativePath.Parse("photos/a.jpg"), TestHash, TestTimestamp, TestTimestamp);

            var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var rootPath = FileTreePaths.GetStagingNodePath(session.StagingRoot, rootId);
            var photosPath = FileTreePaths.GetStagingNodePath(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(photosPath.FullPath)).Single();
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

            entry.Name.ShouldBe(SegmentOf("a.jpg"));
            entry.ContentHash.ShouldBe(TestHash);
            rootPath.ExistsFile.ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_SingleSegmentPath_WritesEntryToRootNodeOnly()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse("a.jpg"), TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var rootPath = FileTreePaths.GetStagingNodePath(session.StagingRoot, rootId);
            var rootEntries = await File.ReadAllLinesAsync(rootPath.FullPath);

            rootEntries.Length.ShouldBe(1);
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(rootEntries.Single());
            entry.Name.ShouldBe(SegmentOf("a.jpg"));
            entry.ContentHash.ShouldBe(TestHash);

            (session.StagingRoot / RelativePath.Root).EnumerateFiles().ShouldHaveSingleItem();
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

            await writer.AppendFileEntryAsync(RelativePath.Parse("photos/2024/a.jpg"), ContentHash.Parse(new string('b', 64)), TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var photos2024Id = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos/2024"));

            var rootEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, rootId).FullPath);
            var photosEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, photosId).FullPath);

            rootEntries.ShouldContain($"{photosId} D photos");
            rootEntries.ShouldNotContain($"{photosId} D photos/");
            photosEntries.ShouldContain($"{photos2024Id} D 2024");
            photosEntries.ShouldNotContain($"{photos2024Id} D 2024/");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_DeeplyNestedPath_SucceedsWithBareDirectoryNamesInOrder()
    {
        const int depth = 6000;

        var prefixes = new RelativePath[depth];
        var currentPath = RelativePath.Root;
        for (var i = 0; i < depth; i++)
        {
            currentPath /= SegmentOf($"d{i}");
            prefixes[i] = currentPath;
        }

        var filePath = currentPath / SegmentOf("a.jpg");

        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(filePath, TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var firstDirectoryId = FileTreePaths.GetStagingDirectoryId(prefixes[0]);
            var secondDirectoryId = FileTreePaths.GetStagingDirectoryId(prefixes[1]);
            var leafParent = prefixes[^1];
            var leafParentId = FileTreePaths.GetStagingDirectoryId(leafParent);

            var rootEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, rootId).FullPath);
            var firstDirectoryEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, firstDirectoryId).FullPath);
            var leafEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, leafParentId).FullPath);

            rootEntries.ShouldContain($"{firstDirectoryId} D d0");
            rootEntries.ShouldNotContain($"{firstDirectoryId} D d0/");
            firstDirectoryEntries.ShouldContain($"{secondDirectoryId} D d1");
            firstDirectoryEntries.ShouldNotContain($"{secondDirectoryId} D d1/");

            var fileEntry = FileTreeSerializer.ParsePersistedFileEntryLine(leafEntries.Single());
            fileEntry.Name.ShouldBe(SegmentOf("a.jpg"));
            fileEntry.ContentHash.ShouldBe(TestHash);
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

            await writer.AppendFileEntryAsync(RelativePath.Parse(" photos/a.jpg "), TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var spacedDirectoryId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse(" photos"));
            var rootEntries = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(session.StagingRoot, rootId).FullPath);
            var nodePath = FileTreePaths.GetStagingNodePath(session.StagingRoot, spacedDirectoryId);
            var line = (await File.ReadAllLinesAsync(nodePath.FullPath)).Single(l => l.Contains(" F ", StringComparison.Ordinal));
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

            rootEntries.ShouldContain($"{spacedDirectoryId} D  photos");
            entry.Name.ShouldBe(SegmentOf("a.jpg "));
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
                await writer.AppendFileEntryAsync(RelativePath.Parse(filePath), TestHash, TestTimestamp, TestTimestamp));
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
                await writer.AppendFileEntryAsync(RelativePath.Parse(filePath), TestHash, TestTimestamp, TestTimestamp));
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
                await writer.AppendFileEntryAsync(RelativePath.Parse(filePath), TestHash, TestTimestamp, TestTimestamp));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_TypedRelativePath_WritesSingleNodeFilePerDirectoryId()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse("photos/a.jpg"), TestHash, TestTimestamp, TestTimestamp);

            var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var photosPath = FileTreePaths.GetStagingNodePath(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(photosPath.FullPath)).Single();
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

            entry.Name.ShouldBe(SegmentOf("a.jpg"));
            entry.ContentHash.ShouldBe(TestHash);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_InvalidFilePath_PreservesExceptionContract()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            var exception = Assert.Throws<ArgumentException>(() => RelativePath.Parse("photos/./a.jpg"));

            exception.ShouldNotBeNull();
            exception.Message.ShouldStartWith("Path segment must be canonical.");
            exception.ParamName.ShouldBe("value");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_RootPath_ThrowsFileNameRequiredMessage()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
        try
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await writer.AppendFileEntryAsync(RelativePath.Root, TestHash, TestTimestamp, TestTimestamp));

            exception.ShouldNotBeNull();
            exception.Message.ShouldStartWith("File path must include a file name.");
            exception.ParamName.ShouldBe("filePath");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
