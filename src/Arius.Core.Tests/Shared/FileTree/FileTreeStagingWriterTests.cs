using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly ContentHash TestHash = ContentHash.Parse(new string('a', 64));

    private static string ResolveStagingNode(LocalDirectory stagingRoot, PathSegment directoryId) =>
        stagingRoot.Resolve(FileTreePaths.GetStagingNodePath(directoryId));

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
    [Arguments("/photos")]
    [Arguments("/photos/2024")]
    [Arguments("C:/photos")]
    [Arguments("C:\\photos")]
    public void GetDirectoryId_RootedPath_Throws(string directoryPath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(directoryPath));
    }

    [Test]
    public void GetDirectoryId_RelativePath_ReturnsStableSegment()
    {
        FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos/2024"))
            .ShouldBe(PathSegment.Parse(HashCodec.ToLowerHex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("photos/2024")))));
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

    [Test]
    public async Task AppendFileEntryAsync_WritesSingleNodeFilePerDirectoryId()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse("photos/a.jpg"), TestHash, TestTimestamp, TestTimestamp);

            var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var photosPath = ResolveStagingNode(session.StagingRoot, photosId);
            var line = (await File.ReadAllLinesAsync(photosPath)).Single();
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

            entry.Name.ShouldBe(PathSegment.Parse("a.jpg"));
            entry.ContentHash.ShouldBe(TestHash);
            new RelativeFileSystem(session.StagingRoot).FileExists(FileTreePaths.GetStagingNodePath(rootId)).ShouldBeTrue();
        });
    }

    [Test]
    public async Task AppendFileEntryAsync_SingleSegmentPath_WritesEntryToRootNodeOnly()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse("a.jpg"), TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var rootPath = ResolveStagingNode(session.StagingRoot, rootId);
            var rootEntries = await File.ReadAllLinesAsync(rootPath);

            rootEntries.Length.ShouldBe(1);
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(rootEntries.Single());
            entry.Name.ShouldBe(PathSegment.Parse("a.jpg"));
            entry.ContentHash.ShouldBe(TestHash);

            Directory.EnumerateFiles(session.StagingRoot.ToString()).ShouldHaveSingleItem();
        });
    }

    [Test]
    public async Task AppendFileEntryAsync_WritesDirectoryEntriesForNestedPath()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse("photos/2024/a.jpg"), ContentHash.Parse(new string('b', 64)), TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var photos2024Id = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos/2024"));

            var rootEntries = await File.ReadAllLinesAsync(ResolveStagingNode(session.StagingRoot, rootId));
            var photosEntries = await File.ReadAllLinesAsync(ResolveStagingNode(session.StagingRoot, photosId));

            rootEntries.ShouldContain($"{photosId} D photos/");
            photosEntries.ShouldContain($"{photos2024Id} D 2024/");
        });
    }

    [Test]
    public async Task AppendFileEntryAsync_PreservesLeadingAndTrailingSpacesInPathSegments()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await writer.AppendFileEntryAsync(RelativePath.Parse(" photos/a.jpg "), TestHash, TestTimestamp, TestTimestamp);

            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var spacedDirectoryId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse(" photos"));
            var rootEntries = await File.ReadAllLinesAsync(ResolveStagingNode(session.StagingRoot, rootId));
            var nodePath = ResolveStagingNode(session.StagingRoot, spacedDirectoryId);
            var line = (await File.ReadAllLinesAsync(nodePath)).Single(l => l.Contains(" F ", StringComparison.Ordinal));
            var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

            rootEntries.ShouldContain($"{spacedDirectoryId} D  photos/");
            entry.Name.ShouldBe(PathSegment.Parse("a.jpg "));
            entry.ContentHash.ShouldBe(TestHash);
        });
    }

    [Test]
    [Arguments(" /a.jpg")]
    [Arguments("photos/   /a.jpg")]
    public void AppendFileEntryAsync_WhitespaceOnlyDirectorySegment_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }

    [Test]
    [Arguments("dir/ ")]
    [Arguments(" ")]
    public void AppendFileEntryAsync_WhitespaceOnlyFileNameSegment_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }

    [Test]
    [Arguments("/photos/a.jpg")]
    [Arguments("C:/photos/a.jpg")]
    [Arguments("C:\\photos\\a.jpg")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos\\a.jpg")]
    public void AppendFileEntryAsync_NonCanonicalPath_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }

    [Test]
    public async Task AppendFileEntryAsync_RootRelativePath_Throws()
    {
        await WithCacheDirectoryAsync(async (cacheDir, _) =>
        {
            await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
            using var writer = new FileTreeStagingWriter(session.StagingRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await writer.AppendFileEntryAsync(RelativePath.Root, TestHash, TestTimestamp, TestTimestamp));
        });
    }
}
