using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests : IDisposable
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly ContentHash TestHash = ContentHash.Parse(new string('a', 64));

    private readonly LocalDirectory _cacheDir;
    private readonly RelativeFileSystem _cacheFileSystem;

    public FileTreeStagingWriterTests()
    {
        _cacheDir = TestTempRoots.CreateDirectory("cache");
        _cacheFileSystem = new RelativeFileSystem(_cacheDir);
    }

    private static async Task<FileTreeEntry[]> ReadNodeEntriesAsync(LocalDirectory stagingRoot, PathSegment directoryId)
        => (await File.ReadAllLinesAsync(stagingRoot.Resolve(FileTreePaths.GetStagingNodePath(directoryId))))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(FileTreeSerializer.ParseStagedNodeEntryLine)
            .ToArray();

    public void Dispose() => _cacheFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);

    [Test]
    public async Task AppendFileEntryAsync_WritesSingleNodeFilePerDirectoryId()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        await writer.AppendFileEntryAsync(RelativePath.Parse("photos/a.jpg"), TestHash, TestTimestamp, TestTimestamp);

        var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
        var photosEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(photosId)));
        var rootEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(rootId)));

        photosEntries.ShouldHaveSingleItem();

        var fileEntry = FileTreeSerializer.ParseStagedNodeEntryLine(photosEntries.Single()).ShouldBeOfType<FileEntry>();
        fileEntry.Name.ShouldBe(PathSegment.Parse("a.jpg"));
        fileEntry.ContentHash.ShouldBe(TestHash);
        fileEntry.Created.ShouldBe(TestTimestamp);
        fileEntry.Modified.ShouldBe(TestTimestamp);

        rootEntries.ShouldHaveSingleItem();
        var directoryEntry = FileTreeSerializer.ParseStagedNodeEntryLine(rootEntries.Single()).ShouldBeOfType<StagedDirectoryEntry>();
        directoryEntry.Name.ShouldBe(PathSegment.Parse("photos"));
        directoryEntry.DirectoryNameHash.ShouldBe(photosId.ToString());
    }

    [Test]
    public async Task AppendFileEntryAsync_SingleSegmentPath_WritesEntryToRootNodeOnly()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        await writer.AppendFileEntryAsync(RelativePath.Parse("a.jpg"), TestHash, TestTimestamp, TestTimestamp);

        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
        var rootPath = session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(rootId));
        var rootEntries = await File.ReadAllLinesAsync(rootPath);

        rootEntries.Length.ShouldBe(1);
        var entry = FileTreeSerializer.ParsePersistedFileEntryLine(rootEntries.Single());
        entry.Name.ShouldBe(PathSegment.Parse("a.jpg"));
        entry.ContentHash.ShouldBe(TestHash);

        Directory.EnumerateFiles(session.StagingRoot.ToString()).ShouldHaveSingleItem();
    }

    [Test]
    public async Task AppendFileEntryAsync_WritesDirectoryEntriesForNestedPath()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        var fileHash = ContentHash.Parse(new string('b', 64));
        await writer.AppendFileEntryAsync(RelativePath.Parse("photos/2024/a.jpg"), fileHash, TestTimestamp, TestTimestamp);

        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
        var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
        var photos2024Id = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos/2024"));

        var rootEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(rootId)));
        var photosEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(photosId)));
        var nestedEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(photos2024Id)));

        rootEntries.ShouldHaveSingleItem();
        FileTreeSerializer.ParseStagedNodeEntryLine(rootEntries.Single()).ShouldBe(new StagedDirectoryEntry
        {
            Name = PathSegment.Parse("photos"),
            DirectoryNameHash = photosId.ToString()
        });

        photosEntries.ShouldHaveSingleItem();
        FileTreeSerializer.ParseStagedNodeEntryLine(photosEntries.Single()).ShouldBe(new StagedDirectoryEntry
        {
            Name = PathSegment.Parse("2024"),
            DirectoryNameHash = photos2024Id.ToString()
        });

        nestedEntries.ShouldHaveSingleItem();
        FileTreeSerializer.ParseStagedNodeEntryLine(nestedEntries.Single()).ShouldBe(new FileEntry
        {
            Name = PathSegment.Parse("a.jpg"),
            ContentHash = fileHash,
            Created = TestTimestamp,
            Modified = TestTimestamp
        });
    }

    [Test]
    public async Task AppendFileEntryAsync_MultipleSequentialAppendsToSameDirectory_PreservesAllFileEntries()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        var secondHash = ContentHash.Parse(new string('b', 64));

        await writer.AppendFileEntryAsync(RelativePath.Parse("photos/a.jpg"), TestHash, TestTimestamp, TestTimestamp);
        await writer.AppendFileEntryAsync(RelativePath.Parse("photos/b.jpg"), secondHash, TestTimestamp, TestTimestamp);

        var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
        var entries = (await ReadNodeEntriesAsync(session.StagingRoot, photosId)).OfType<FileEntry>().ToArray();

        entries.Length.ShouldBe(2);
        entries.Select(static entry => entry.Name).ShouldBe([PathSegment.Parse("a.jpg"), PathSegment.Parse("b.jpg")], ignoreOrder: true);
        entries.Select(static entry => entry.ContentHash).ShouldBe([TestHash, secondHash], ignoreOrder: true);
    }

    [Test]
    public async Task AppendFileEntryAsync_ConcurrentAppendsToSameDirectory_PreservesAllFileEntries()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        var expectedEntries = Enumerable.Range(0, 24)
            .Select(index => new
            {
                Path = RelativePath.Parse($"photos/file-{index:D2}.jpg"),
                Hash = ContentHash.Parse(index.ToString("x64"))
            })
            .ToArray();

        await Task.WhenAll(expectedEntries.Select(entry =>
            writer.AppendFileEntryAsync(entry.Path, entry.Hash, TestTimestamp, TestTimestamp)));

        var photosId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
        var entries = (await ReadNodeEntriesAsync(session.StagingRoot, photosId)).OfType<FileEntry>().ToArray();

        entries.Length.ShouldBe(expectedEntries.Length);

        foreach (var expectedEntry in expectedEntries)
        {
            entries.ShouldContain(entry =>
                entry.Name == expectedEntry.Path.Name
                && entry.ContentHash == expectedEntry.Hash
                && entry.Created == TestTimestamp
                && entry.Modified == TestTimestamp);
        }
    }

    [Test]
    public async Task AppendFileEntryAsync_PreservesLeadingAndTrailingSpacesInPathSegments()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        await writer.AppendFileEntryAsync(RelativePath.Parse(" photos/a.jpg "), TestHash, TestTimestamp, TestTimestamp);

        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
        var spacedDirectoryId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse(" photos"));
        var rootEntries = await File.ReadAllLinesAsync(session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(rootId)));
        var nodePath = session.StagingRoot.Resolve(FileTreePaths.GetStagingNodePath(spacedDirectoryId));
        var line = (await File.ReadAllLinesAsync(nodePath)).Single(l => l.Contains(" F ", StringComparison.Ordinal));
        var entry = FileTreeSerializer.ParsePersistedFileEntryLine(line);

        rootEntries.ShouldContain($"{spacedDirectoryId} D  photos/");
        entry.Name.ShouldBe(PathSegment.Parse("a.jpg "));
        entry.ContentHash.ShouldBe(TestHash);
    }

    [Test]
    public async Task AppendFileEntryAsync_RootRelativePath_Throws()
    {
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.AppendFileEntryAsync(RelativePath.Root, TestHash, TestTimestamp, TestTimestamp));
    }
}
