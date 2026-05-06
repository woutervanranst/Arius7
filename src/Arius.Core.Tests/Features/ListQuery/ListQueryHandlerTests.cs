using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Testing;
using ListQueryType = Arius.Core.Features.ListQuery.ListQuery;

namespace Arius.Core.Tests.Features.ListQuery;

public class ListQueryHandlerTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();
    private static readonly DateTimeOffset s_created = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Handle_CloudOnlyNonRecursive_StreamsRootDirectoryEntries()
    {
        var rootTree = Entries(
            DirectoryEntryOf(SegmentOf("docs"), TreeHashFor("docs")),
            FileEntryOf(SegmentOf("readme.txt"), ContentHashFor("readme"), s_created, s_modified));
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption),
            FileCount = 1,
            TotalSize = 123,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-1", "ctr-ls-test-1", s_encryption);
        fixture.Index.AddEntry(new ShardEntry(ContentHashFor("readme"), FakeChunkHash('c'), 123, 50));
        var handler = fixture.CreateListQueryHandler();

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);

        var directory = results.OfType<RepositoryDirectoryEntry>().Single();
        directory.RelativePath.ShouldBe(PathOf("docs"));
        directory.ExistsInCloud.ShouldBeTrue();
        directory.ExistsLocally.ShouldBeFalse();
        directory.TreeHash.ShouldBe(TreeHashFor("docs"));

        var file = results.OfType<RepositoryFileEntry>().Single();
        file.RelativePath.ShouldBe(PathOf("readme.txt"));
        file.ContentHash.ShouldBe(ContentHashFor("readme"));
        file.OriginalSize.ShouldBe(123);
        file.Created.ShouldBe(s_created);
        file.Modified.ShouldBe(s_modified);
        file.ExistsInCloud.ShouldBeTrue();
        file.ExistsLocally.ShouldBeFalse();
        file.HasPointerFile.ShouldBeNull();
        file.BinaryExists.ShouldBeNull();
    }

    [Test]
    public async Task Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix()
    {
        var docsTree = Entries(
            DirectoryEntryOf(SegmentOf("nested"), FakeFileTreeHash('d')),
            FileEntryOf(SegmentOf("guide.txt"), FakeContentHash('e'), s_created, s_modified));

        var docsHash = FileTreeBuilder.ComputeHash(docsTree, s_encryption);
        var rootTree = Entries(
            DirectoryEntryOf(SegmentOf("docs"), docsHash),
            FileEntryOf(SegmentOf("root.txt"), FakeContentHash('f'), s_created, s_modified));

        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption),
            FileCount = 2,
            TotalSize = 456,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, docsTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-2", "ctr-ls-test-2", s_encryption);
        fixture.Index.AddEntry(new ShardEntry(FakeContentHash('e'), FakeChunkHash('1'), 456, 200));
        var handler = fixture.CreateListQueryHandler();

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Prefix = PathOf("docs"), Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);
        results.Select(entry => entry.RelativePath).OrderBy(path => path.ToString()).ShouldBe([PathOf("docs/guide.txt"), PathOf("docs/nested")]);
        results.ShouldNotContain(entry => entry.RelativePath == PathOf("root.txt"));
    }

    [Test]
    public async Task Handle_WithLocalPath_MergesCloudAndLocalFilesInOneDirectory()
    {
        var tempRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), $"arius-ls-{Guid.NewGuid():N}"));
        tempRoot.CreateDirectory();

        try
        {
            await (tempRoot / RelativePath.Parse("shared.txt")).WriteAllTextAsync("local-shared");
            await (tempRoot / RelativePath.Parse("shared.txt").ToPointerFilePath()).WriteAllTextAsync(FakeContentHash('2').ToString());
            await (tempRoot / RelativePath.Parse("local-only.txt")).WriteAllTextAsync("local-only");

            var rootTree = Entries(
                FileEntryOf(SegmentOf("cloud-only.txt"), ContentHashFor("cloud-only"), s_created, s_modified),
                FileEntryOf(SegmentOf("shared.txt"), ContentHashFor("shared"), s_created, s_modified));
            var snapshot = new SnapshotManifest
            {
                Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
                RootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption),
                FileCount = 2,
                TotalSize = 100,
                AriusVersion = "test"
            };

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-3", "ctr-ls-test-3", s_encryption);
            fixture.Index.AddEntry(new ShardEntry(ContentHashFor("cloud-only"), FakeChunkHash('4'), 10, 5));
            fixture.Index.AddEntry(new ShardEntry(ContentHashFor("shared"), FakeChunkHash('5'), 20, 10));
            var handler = fixture.CreateListQueryHandler();

            var results = new List<RepositoryFileEntry>();
            await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { LocalPath = tempRoot, Recursive = false }), CancellationToken.None))
            {
                if (entry is RepositoryFileEntry file)
                {
                    results.Add(file);
                }
            }

            results.Count.ShouldBe(3);

            var shared = results.Single(file => file.RelativePath == PathOf("shared.txt"));
            shared.ExistsInCloud.ShouldBeTrue();
            shared.ExistsLocally.ShouldBeTrue();
            shared.HasPointerFile.ShouldBe(true);
            shared.BinaryExists.ShouldBe(true);
            shared.OriginalSize.ShouldBe(20);

            var cloudOnly = results.Single(file => file.RelativePath == PathOf("cloud-only.txt"));
            cloudOnly.ExistsInCloud.ShouldBeTrue();
            cloudOnly.ExistsLocally.ShouldBeFalse();
            cloudOnly.HasPointerFile.ShouldBeNull();
            cloudOnly.BinaryExists.ShouldBeNull();
            cloudOnly.OriginalSize.ShouldBe(10);

            var localOnly = results.Single(file => file.RelativePath == PathOf("local-only.txt"));
            localOnly.ExistsInCloud.ShouldBeFalse();
            localOnly.ExistsLocally.ShouldBeTrue();
            localOnly.HasPointerFile.ShouldBe(false);
            localOnly.BinaryExists.ShouldBe(true);
            localOnly.ContentHash.ShouldBeNull();
        }
        finally
        {
            if (tempRoot.ExistsDirectory)
            {
                tempRoot.DeleteDirectory(recursive: true);
            }
        }
    }

    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService("ls");
        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-missing", "ctr-ls-missing", cacheBudgetBytes: 1024 * 1024);
        var fileTreeService = new FileTreeService(blobs, s_encryption, index, "acct-ls-missing", "ctr-ls-missing");
        var snapshotSvc = new SnapshotService(blobs, s_encryption, "acct-ls-missing", "ctr-ls-missing");
        var logger = new FakeLogger<ListQueryHandler>();
        var handler = new ListQueryHandler(index, fileTreeService, snapshotSvc, logger, "acct-ls-missing", "ctr-ls-missing");

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQueryType(new ListQueryOptions()), CancellationToken.None))
            {
            }
        });

        ex.Message.ShouldBe("No snapshots found in this repository.");
        blobs.CreateCalled.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_RecursiveFalse_YieldsOnlyImmediateChildren()
    {
        var childTree = Entries(FileEntryOf(SegmentOf("deep.txt"), FakeContentHash('6'), s_created, s_modified));
        var childHash = FileTreeBuilder.ComputeHash(childTree, s_encryption);
        var rootTree = Entries(
            DirectoryEntryOf(SegmentOf("child"), childHash),
            FileEntryOf(SegmentOf("root.txt"), FakeContentHash('7'), s_created, s_modified));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, childTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-33-nr", "ctr-33-nr", s_encryption);
        var handler = fixture.CreateListQueryHandler();

        var nonRecursive = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None).ToListAsync();
        nonRecursive.Count.ShouldBe(2);
        nonRecursive.ShouldContain(e => e.RelativePath == PathOf("child"));
        nonRecursive.ShouldContain(e => e.RelativePath == PathOf("root.txt"));
        nonRecursive.ShouldNotContain(e => e.RelativePath == PathOf("child/deep.txt"));

        await using var fixture2 = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-33-r", "ctr-33-r", s_encryption);
        var handler2 = fixture2.CreateListQueryHandler();

        var recursive = await handler2.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None).ToListAsync();
        recursive.ShouldContain(e => e.RelativePath == PathOf("child"));
        recursive.ShouldContain(e => e.RelativePath == PathOf("root.txt"));
        recursive.ShouldContain(e => e.RelativePath == PathOf("child/deep.txt"));
    }

    [Test]
    public async Task Handle_FilenameFilter_CaseInsensitiveMatchOnFilesNotDirs()
    {
        var rootTree = Entries(
            DirectoryEntryOf(SegmentOf("Photos"), FakeFileTreeHash('8')),
            FileEntryOf(SegmentOf("VACATION.jpg"), FakeContentHash('9'), s_created, s_modified),
            FileEntryOf(SegmentOf("sunset.jpg"), FakeContentHash('a'), s_created, s_modified),
            FileEntryOf(SegmentOf("readme.txt"), FakeContentHash('c'), s_created, s_modified));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-36", "ctr-36", s_encryption);
        var handler = fixture.CreateListQueryHandler();

        // Filter "vacation" should match VACATION.jpg (case-insensitive), not sunset or readme
        var results = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, Filter = "vacation" }), CancellationToken.None).ToListAsync();

         // Directories are NOT filtered — Photos/ should still appear
        results.ShouldContain(e => e.RelativePath == PathOf("Photos"));
        results.ShouldContain(e => e.RelativePath == PathOf("VACATION.jpg"));
        results.ShouldNotContain(e => e.RelativePath == PathOf("sunset.jpg"));
        results.ShouldNotContain(e => e.RelativePath == PathOf("readme.txt"));
    }

    [Test]
    public async Task Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags()
    {
        var tempRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), $"arius-ls-38-{Guid.NewGuid():N}"));
        (tempRoot / RelativePath.Parse("cloud-local-dir")).CreateDirectory();
        (tempRoot / RelativePath.Parse("local-only-dir")).CreateDirectory();

        try
        {
            IReadOnlyList<FileTreeEntry> cloudLocalTree = [];
            IReadOnlyList<FileTreeEntry> cloudOnlyTree = [];
            var cloudLocalHash = FileTreeBuilder.ComputeHash(cloudLocalTree, s_encryption);
            var cloudOnlyHash = FileTreeBuilder.ComputeHash(cloudOnlyTree, s_encryption);

            // root has: cloud+local dir, cloud-only dir; local has: local-only dir
            var rootTree = Entries(
                DirectoryEntryOf(SegmentOf("cloud-local-dir"), cloudLocalHash),
                DirectoryEntryOf(SegmentOf("cloud-only-dir"), cloudOnlyHash));
            var rootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption);
            var snapshot = MakeSnapshot(rootHash);

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            await SeedTreeAsync(blobs, cloudLocalTree);
            await SeedTreeAsync(blobs, cloudOnlyTree);
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-38", "ctr-38", s_encryption);
            var handler = fixture.CreateListQueryHandler();

            var dirs = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, LocalPath = tempRoot }), CancellationToken.None)
                .OfType<RepositoryDirectoryEntry>()
                .ToListAsync();

            dirs.Count.ShouldBe(3);

            var cloudLocal = dirs.Single(d => d.RelativePath == PathOf("cloud-local-dir"));
            cloudLocal.ExistsInCloud.ShouldBeTrue();
            cloudLocal.ExistsLocally.ShouldBeTrue();

            var cloudOnly = dirs.Single(d => d.RelativePath == PathOf("cloud-only-dir"));
            cloudOnly.ExistsInCloud.ShouldBeTrue();
            cloudOnly.ExistsLocally.ShouldBeFalse();

            var localOnly = dirs.Single(d => d.RelativePath == PathOf("local-only-dir"));
            localOnly.ExistsInCloud.ShouldBeFalse();
            localOnly.ExistsLocally.ShouldBeTrue();
        }
        finally
        {
            if (tempRoot.ExistsDirectory)
                tempRoot.DeleteDirectory(recursive: true);
        }
    }

    [Test]
    public async Task Handle_BatchSizeLookup_CalledOncePerDirectory_SizeNullWhenNotInIndex()
    {
        var childTree = Entries(FileEntryOf(SegmentOf("child-file.txt"), FakeContentHash('d'), s_created, s_modified));
        var childHash = FileTreeBuilder.ComputeHash(childTree, s_encryption);
        var rootTree = Entries(
            DirectoryEntryOf(SegmentOf("child"), childHash),
            FileEntryOf(SegmentOf("known.txt"), ContentHashFor("known"), s_created, s_modified),
            FileEntryOf(SegmentOf("unknown.txt"), FakeContentHash('f'), s_created, s_modified));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, childTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-39", "ctr-39", s_encryption);
        fixture.Index.AddEntry(new ShardEntry(ContentHashFor("known"), FakeChunkHash('b'), 999, 500));

        var handler = fixture.CreateListQueryHandler();
        var files   = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None)
            .OfType<RepositoryFileEntry>()
            .ToListAsync();

        var known   = files.Single(f => f.RelativePath == PathOf("known.txt"));
        var unknown = files.Single(f => f.RelativePath == PathOf("unknown.txt"));
        var child   = files.Single(f => f.RelativePath == PathOf("child/child-file.txt"));

        known.OriginalSize.ShouldBe(999);
        unknown.OriginalSize.ShouldBeNull();
        child.OriginalSize.ShouldBeNull();
    }

    [Test]
    public async Task Handle_NoSnapshots_ThrowsInvalidOperationException()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-310", "ctr-310", s_encryption);
        var handler = fixture.CreateListQueryHandler();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQueryType(new ListQueryOptions()), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("snapshot", Case.Insensitive);
    }

    [Test]
    public async Task Handle_SpecificVersionNotFound_ThrowsWithDescriptiveMessage()
    {
        IReadOnlyList<FileTreeEntry> rootTree = [];
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-310b", "ctr-310b", s_encryption);
        var handler = fixture.CreateListQueryHandler();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQueryType(new ListQueryOptions { Version = "9999-not-found" }), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("9999-not-found");
    }

    [Test]
    public async Task Handle_CancellationRequested_StopsEnumeration()
    {
        // Build a deep tree: root → dir1 → dir2 → ... → dir10.
        // Each level is a separate WalkDirectoryAsync call, so cancellation is
        // checked at each level boundary (ThrowIfCancellationRequested at top of method).
        IReadOnlyList<FileTreeEntry> leafTree = [];
        var leafHash = FileTreeBuilder.ComputeHash(leafTree, s_encryption);

        // Build chain: level10 → level9 → … → level1 → root
        var currentHash  = leafHash;
        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, leafTree);

        for (var i = 10; i >= 1; i--)
        {
            var tree = Entries(
                DirectoryEntryOf(SegmentOf($"level{i + 1}"), currentHash),
                FileEntryOf(SegmentOf($"file{i}.txt"), FakeContentHash("123456789a"[10 - i]), s_created, s_modified));
            currentHash = FileTreeBuilder.ComputeHash(tree, s_encryption);
            await SeedTreeAsync(blobs, tree);
        }

        var snapshot = MakeSnapshot(currentHash);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-311", "ctr-311", s_encryption);
        var handler = fixture.CreateListQueryHandler();

        using var cts = new CancellationTokenSource();
        var collected = new List<RepositoryEntry>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), cts.Token))
            {
                collected.Add(entry);
                if (collected.Count >= 2)
                    await cts.CancelAsync();
            }
        });

        // Should have stopped well before all 10 levels were traversed
        collected.Count.ShouldBeLessThan(20);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SnapshotManifest MakeSnapshot(FileTreeHash rootHash) => new()
    {
        Timestamp  = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
        RootHash   = rootHash,
        FileCount  = 0,
        TotalSize  = 0,
        AriusVersion = "test"
    };

    private static ContentHash ContentHashFor(string label) => s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label));

    private static FileTreeHash TreeHashFor(string label) => FileTreeHash.Parse(ContentHashFor(label));

    private static IReadOnlyList<FileTreeEntry> Entries(params FileTreeEntry[] entries) => entries;

    private static async Task<FileTreeHash> SeedTreeAsync(FakeSeededBlobContainerService blobs, IReadOnlyList<FileTreeEntry> entries)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload = (Hash: FileTreeHash.Parse(s_encryption.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);
        using var ms = new MemoryStream();

        await using (var encStream = s_encryption.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzipStream.WriteAsync(payload.Plaintext);
        }

        blobs.AddBlob(BlobPaths.FileTree(payload.Hash), ms.ToArray());
        return payload.Hash;
    }

}
