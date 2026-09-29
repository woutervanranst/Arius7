using Arius.Core.Shared.FileTree;
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
    public async Task AppendFileEntryAsync_AfterRetargetFailure_StillWritesToTheOriginalNode()
    {
        // A stripe holds one open append handle. When a write to a *different* node retargets that stripe,
        // the old handle is disposed before the new one is opened — so if opening fails (disk full, denied,
        // too many handles) the stripe must not be left pointing at the disposed handle. Stage 5b has no
        // per-file catch and Parallel.ForEachAsync keeps already-started iterations running, so a later
        // append on that stripe would otherwise die with ObjectDisposedException instead of the real error.
        await using var session = await FileTreeStagingSession.OpenAsync(_cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);

        var (first, second) = FindDirectoriesSharingAStripe();

        // Establish the stripe's handle on `first`.
        await writer.AppendFileEntryAsync(first / PathSegment.Parse("a.bin"), TestHash, TestTimestamp, TestTimestamp);

        // Make opening `second`'s node file impossible by occupying its path with a directory.
        var secondNode = FileTreePaths.GetStagingNodePath(FileTreePaths.GetStagingDirectoryId(second));
        Directory.CreateDirectory(session.StagingRoot.Resolve(secondNode));

        await Should.ThrowAsync<Exception>(() =>
            writer.AppendFileEntryAsync(second / PathSegment.Parse("b.bin"), TestHash, TestTimestamp, TestTimestamp));

        // The stripe must have recovered: appending to the original node still works.
        await writer.AppendFileEntryAsync(first / PathSegment.Parse("c.bin"), TestHash, TestTimestamp, TestTimestamp);

        var entries = await ReadNodeEntriesAsync(session.StagingRoot, FileTreePaths.GetStagingDirectoryId(first));
        entries.Select(e => e.Name.ToString()).ShouldBe(["a.bin", "c.bin"], ignoreOrder: true);
    }

    /// <summary>
    /// Two directories whose staging node paths land on the same lock stripe. String hashing is randomized
    /// per process, so the pair is found at runtime rather than hard-coded.
    /// </summary>
    private static (RelativePath First, RelativePath Second) FindDirectoriesSharingAStripe()
    {
        const int stripeCount = 256;
        var seen = new Dictionary<int, RelativePath>();

        for (var i = 0; i < 100_000; i++)
        {
            var directory = RelativePath.Parse($"d{i}");
            var node      = FileTreePaths.GetStagingNodePath(FileTreePaths.GetStagingDirectoryId(directory));
            var stripe    = (int)((uint)node.GetHashCode() % stripeCount);

            if (seen.TryGetValue(stripe, out var previous))
                return (previous, directory);

            seen[stripe] = directory;
        }

        throw new InvalidOperationException("No stripe collision found.");
    }

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
