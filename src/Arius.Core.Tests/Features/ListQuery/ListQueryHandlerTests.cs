using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
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
        var rootTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "docs/", Type = FileTreeEntryType.Dir, Hash = HashFor("docs") },
                new FileTreeEntry { Name = "readme.txt", Type = FileTreeEntryType.File, Hash = HashFor("readme"), Created = s_created, Modified = s_modified }
            ]
        };

        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = rootHash,
            FileCount = 1,
            TotalSize = 123,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-1", "ctr-ls-test-1", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("readme"), HashFor("chunk"), 123, 50));

        var fileTreeService   = new FileTreeService(blobs, s_encryption, index, "acct-ls-test-1", "ctr-ls-test-1");
        var snapshotSvc = new SnapshotService(blobs, s_encryption, "acct-ls-test-1", "ctr-ls-test-1");
        var handler = new ListQueryHandler(
            index,
            fileTreeService,
            snapshotSvc,
            NullLogger<ListQueryHandler>.Instance,
            "acct-ls-test-1",
            "ctr-ls-test-1");

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);

        var directory = results.OfType<RepositoryDirectoryEntry>().Single();
        directory.RelativePath.ShouldBe("docs/");
        directory.ExistsInCloud.ShouldBeTrue();
        directory.ExistsLocally.ShouldBeFalse();
        directory.TreeHash.ShouldBe(HashFor("docs"));

        var file = results.OfType<RepositoryFileEntry>().Single();
        file.RelativePath.ShouldBe("readme.txt");
        file.ContentHash.ShouldBe(HashFor("readme"));
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
        var docsTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "nested/", Type = FileTreeEntryType.Dir, Hash = HashFor("nested") },
                new FileTreeEntry { Name = "guide.txt", Type = FileTreeEntryType.File, Hash = HashFor("guide"), Created = s_created, Modified = s_modified }
            ]
        };

        var docsHash = FileTreeBlobSerializer.ComputeHash(docsTree, s_encryption);
        var rootTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "docs/", Type = FileTreeEntryType.Dir, Hash = docsHash },
                new FileTreeEntry { Name = "root.txt", Type = FileTreeEntryType.File, Hash = HashFor("root"), Created = s_created, Modified = s_modified }
            ]
        };

        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = rootHash,
            FileCount = 2,
            TotalSize = 456,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(docsHash), await FileTreeBlobSerializer.SerializeForStorageAsync(docsTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-2", "ctr-ls-test-2", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("guide"), HashFor("chunk-guide"), 456, 200));

        var treeCache2   = new FileTreeService(blobs, s_encryption, index, "acct-ls-test-2", "ctr-ls-test-2");
        var snapshotSvc2 = new SnapshotService(blobs, s_encryption, "acct-ls-test-2", "ctr-ls-test-2");
        var handler = new ListQueryHandler(
            index,
            treeCache2,
            snapshotSvc2,
            NullLogger<ListQueryHandler>.Instance,
            "acct-ls-test-2",
            "ctr-ls-test-2");

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Prefix = "docs", Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);
        results.Select(entry => entry.RelativePath).OrderBy(path => path).ShouldBe(["docs/guide.txt", "docs/nested/"]);
        results.ShouldNotContain(entry => entry.RelativePath == "root.txt");
    }

    [Test]
    public async Task Handle_WithLocalPath_MergesCloudAndLocalFilesInOneDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-ls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "shared.txt"), "local-shared");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "shared.txt.pointer.arius"), HashFor("shared"));
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "local-only.txt"), "local-only");

            var rootTree = new FileTreeBlob
            {
                Entries =
                [
                    new FileTreeEntry { Name = "cloud-only.txt", Type = FileTreeEntryType.File, Hash = HashFor("cloud-only"), Created = s_created, Modified = s_modified },
                    new FileTreeEntry { Name = "shared.txt", Type = FileTreeEntryType.File, Hash = HashFor("shared"), Created = s_created, Modified = s_modified }
                ]
            };

            var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
            var snapshot = new SnapshotManifest
            {
                Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
                RootHash = rootHash,
                FileCount = 2,
                TotalSize = 100,
                AriusVersion = "test"
            };

            var blobs = new FakeSeededBlobContainerService();
            blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-3", "ctr-ls-test-3", cacheBudgetBytes: 1024 * 1024);
            index.RecordEntry(new ShardEntry(HashFor("cloud-only"), HashFor("chunk-cloud"), 10, 5));
            index.RecordEntry(new ShardEntry(HashFor("shared"), HashFor("chunk-shared"), 20, 10));

            var treeCache3   = new FileTreeService(blobs, s_encryption, index, "acct-ls-test-3", "ctr-ls-test-3");
            var snapshotSvc3 = new SnapshotService(blobs, s_encryption, "acct-ls-test-3", "ctr-ls-test-3");
            var handler = new ListQueryHandler(
                index,
                treeCache3,
                snapshotSvc3,
                NullLogger<ListQueryHandler>.Instance,
                "acct-ls-test-3",
                "ctr-ls-test-3");

            var results = new List<RepositoryFileEntry>();
            await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { LocalPath = tempRoot, Recursive = false }), CancellationToken.None))
            {
                if (entry is RepositoryFileEntry file)
                {
                    results.Add(file);
                }
            }

            results.Count.ShouldBe(3);

            var shared = results.Single(file => file.RelativePath == "shared.txt");
            shared.ExistsInCloud.ShouldBeTrue();
            shared.ExistsLocally.ShouldBeTrue();
            shared.HasPointerFile.ShouldBe(true);
            shared.BinaryExists.ShouldBe(true);
            shared.OriginalSize.ShouldBe(20);

            var cloudOnly = results.Single(file => file.RelativePath == "cloud-only.txt");
            cloudOnly.ExistsInCloud.ShouldBeTrue();
            cloudOnly.ExistsLocally.ShouldBeFalse();
            cloudOnly.HasPointerFile.ShouldBeNull();
            cloudOnly.BinaryExists.ShouldBeNull();
            cloudOnly.OriginalSize.ShouldBe(10);

            var localOnly = results.Single(file => file.RelativePath == "local-only.txt");
            localOnly.ExistsInCloud.ShouldBeFalse();
            localOnly.ExistsLocally.ShouldBeTrue();
            localOnly.HasPointerFile.ShouldBe(false);
            localOnly.BinaryExists.ShouldBe(true);
            localOnly.ContentHash.ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService();
        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-missing", "ctr-ls-missing", cacheBudgetBytes: 1024 * 1024);
        var fileTreeService = new FileTreeService(blobs, s_encryption, index, "acct-ls-missing", "ctr-ls-missing");
        var snapshotSvc = new SnapshotService(blobs, s_encryption, "acct-ls-missing", "ctr-ls-missing");
        var handler = new ListQueryHandler(index, fileTreeService, snapshotSvc, NullLogger<ListQueryHandler>.Instance, "acct-ls-missing", "ctr-ls-missing");

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
        var childTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "deep.txt", Type = FileTreeEntryType.File, Hash = HashFor("deep"), Created = s_created, Modified = s_modified }
            ]
        };
        var childHash = FileTreeBlobSerializer.ComputeHash(childTree, s_encryption);
        var rootTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "child/", Type = FileTreeEntryType.Dir, Hash = childHash },
                new FileTreeEntry { Name = "root.txt", Type = FileTreeEntryType.File, Hash = HashFor("root"), Created = s_created, Modified = s_modified }
            ]
        };
        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(childHash), await FileTreeBlobSerializer.SerializeForStorageAsync(childTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-33-nr", "ctr-33-nr", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var nonRecursive = await CollectAsync(handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None));
        nonRecursive.Count.ShouldBe(2);
        nonRecursive.ShouldContain(e => e.RelativePath == "child/");
        nonRecursive.ShouldContain(e => e.RelativePath == "root.txt");
        nonRecursive.ShouldNotContain(e => e.RelativePath == "child/deep.txt");

        using var index2 = new ChunkIndexService(blobs, s_encryption, "acct-33-r", "ctr-33-r", cacheBudgetBytes: 1024 * 1024);
        var handler2 = MakeHandler(blobs, index2);

        var recursive = await CollectAsync(handler2.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None));
        recursive.ShouldContain(e => e.RelativePath == "child/");
        recursive.ShouldContain(e => e.RelativePath == "root.txt");
        recursive.ShouldContain(e => e.RelativePath == "child/deep.txt");
    }

    [Test]
    public async Task Handle_FilenameFilter_CaseInsensitiveMatchOnFilesNotDirs()
    {
        var rootTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "Photos/", Type = FileTreeEntryType.Dir, Hash = HashFor("photos-dir") },
                new FileTreeEntry { Name = "VACATION.jpg", Type = FileTreeEntryType.File, Hash = HashFor("vac"), Created = s_created, Modified = s_modified },
                new FileTreeEntry { Name = "sunset.jpg",   Type = FileTreeEntryType.File, Hash = HashFor("sun"), Created = s_created, Modified = s_modified },
                new FileTreeEntry { Name = "readme.txt",   Type = FileTreeEntryType.File, Hash = HashFor("rdm"), Created = s_created, Modified = s_modified },
            ]
        };
        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-36", "ctr-36", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index, "acct-36", "ctr-36");

        var results = await CollectAsync(handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, Filter = "vacation" }), CancellationToken.None));

        results.ShouldContain(e => e.RelativePath == "Photos/");
        results.ShouldContain(e => e.RelativePath == "VACATION.jpg");
        results.ShouldNotContain(e => e.RelativePath == "sunset.jpg");
        results.ShouldNotContain(e => e.RelativePath == "readme.txt");
    }

    [Test]
    public async Task Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-ls-38-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "cloud-local-dir"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "local-only-dir"));

        try
        {
            var cloudLocalTree  = new FileTreeBlob { Entries = [] };
            var cloudOnlyTree   = new FileTreeBlob { Entries = [] };
            var cloudLocalHash  = FileTreeBlobSerializer.ComputeHash(cloudLocalTree, s_encryption);
            var cloudOnlyHash   = FileTreeBlobSerializer.ComputeHash(cloudOnlyTree, s_encryption);

            var rootTree = new FileTreeBlob
            {
                Entries =
                [
                    new FileTreeEntry { Name = "cloud-local-dir/", Type = FileTreeEntryType.Dir, Hash = cloudLocalHash },
                    new FileTreeEntry { Name = "cloud-only-dir/",  Type = FileTreeEntryType.Dir, Hash = cloudOnlyHash },
                ]
            };
            var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
            var snapshot = MakeSnapshot(rootHash);

            var blobs = new FakeSeededBlobContainerService();
            blobs.AddBlob(BlobPaths.FileTree(rootHash),       await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
            blobs.AddBlob(BlobPaths.FileTree(cloudLocalHash), await FileTreeBlobSerializer.SerializeForStorageAsync(cloudLocalTree, s_encryption));
            blobs.AddBlob(BlobPaths.FileTree(cloudOnlyHash),  await FileTreeBlobSerializer.SerializeForStorageAsync(cloudOnlyTree, s_encryption));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            using var index = new ChunkIndexService(blobs, s_encryption, "acct-38", "ctr-38", cacheBudgetBytes: 1024 * 1024);
            var handler = MakeHandler(blobs, index, "acct-38", "ctr-38");

            var results = await CollectAsync(handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, LocalPath = tempRoot }), CancellationToken.None));
            var dirs = results.OfType<RepositoryDirectoryEntry>().ToList();

            dirs.Count.ShouldBe(3);

            var cloudLocal = dirs.Single(d => d.RelativePath == "cloud-local-dir/");
            cloudLocal.ExistsInCloud.ShouldBeTrue();
            cloudLocal.ExistsLocally.ShouldBeTrue();

            var cloudOnly = dirs.Single(d => d.RelativePath == "cloud-only-dir/");
            cloudOnly.ExistsInCloud.ShouldBeTrue();
            cloudOnly.ExistsLocally.ShouldBeFalse();

            var localOnly = dirs.Single(d => d.RelativePath == "local-only-dir/");
            localOnly.ExistsInCloud.ShouldBeFalse();
            localOnly.ExistsLocally.ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task Handle_BatchSizeLookup_CalledOncePerDirectory_SizeNullWhenNotInIndex()
    {
        var childTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "child-file.txt", Type = FileTreeEntryType.File, Hash = HashFor("child-file"), Created = s_created, Modified = s_modified }
            ]
        };
        var childHash = FileTreeBlobSerializer.ComputeHash(childTree, s_encryption);
        var rootTree = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "child/",    Type = FileTreeEntryType.Dir,  Hash = childHash },
                new FileTreeEntry { Name = "known.txt", Type = FileTreeEntryType.File, Hash = HashFor("known"),   Created = s_created, Modified = s_modified },
                new FileTreeEntry { Name = "unknown.txt",Type = FileTreeEntryType.File, Hash = HashFor("unknown"), Created = s_created, Modified = s_modified },
            ]
        };
        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash),  await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(childHash), await FileTreeBlobSerializer.SerializeForStorageAsync(childTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-39", "ctr-39", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("known"), HashFor("chunk-known"), 999, 500));

        var handler = MakeHandler(blobs, index, "acct-39", "ctr-39");
        var results = await CollectAsync(handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None));
        var files = results.OfType<RepositoryFileEntry>().ToList();

        var known   = files.Single(f => f.RelativePath == "known.txt");
        var unknown = files.Single(f => f.RelativePath == "unknown.txt");
        var child   = files.Single(f => f.RelativePath == "child/child-file.txt");

        known.OriginalSize.ShouldBe(999);
        unknown.OriginalSize.ShouldBeNull();
        child.OriginalSize.ShouldBeNull();
    }

    [Test]
    public async Task Handle_NoSnapshots_ThrowsInvalidOperationException()
    {
        var blobs = new FakeSeededBlobContainerService();
        using var index = new ChunkIndexService(blobs, s_encryption, "acct-310", "ctr-310", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQueryType(new ListQueryOptions()), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("snapshot", Case.Insensitive);
    }

    [Test]
    public async Task Handle_SpecificVersionNotFound_ThrowsWithDescriptiveMessage()
    {
        var rootTree = new FileTreeBlob { Entries = [] };
        var rootHash = FileTreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await FileTreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-310b", "ctr-310b", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQueryType(new ListQueryOptions { Version = "9999-not-found" }), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("9999-not-found");
    }

    [Test]
    public async Task Handle_CancellationRequested_StopsEnumeration()
    {
        var leafTree = new FileTreeBlob { Entries = [] };
        var leafHash = FileTreeBlobSerializer.ComputeHash(leafTree, s_encryption);

        var currentHash  = leafHash;
        var blobs = new FakeSeededBlobContainerService();
        blobs.AddBlob(BlobPaths.FileTree(leafHash), await FileTreeBlobSerializer.SerializeForStorageAsync(leafTree, s_encryption));

        for (var i = 10; i >= 1; i--)
        {
            var tree = new FileTreeBlob
            {
                Entries =
                [
                    new FileTreeEntry { Name = $"level{i + 1}/", Type = FileTreeEntryType.Dir, Hash = currentHash },
                    new FileTreeEntry { Name = $"file{i}.txt",   Type = FileTreeEntryType.File, Hash = HashFor($"f{i}"), Created = s_created, Modified = s_modified }
                ]
            };
            currentHash = FileTreeBlobSerializer.ComputeHash(tree, s_encryption);
            blobs.AddBlob(BlobPaths.FileTree(currentHash), await FileTreeBlobSerializer.SerializeForStorageAsync(tree, s_encryption));
        }

        var snapshot = MakeSnapshot(currentHash);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-311", "ctr-311", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

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

        collected.Count.ShouldBeLessThan(20);
    }

    private static SnapshotManifest MakeSnapshot(string rootHash) => new()
    {
        Timestamp  = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
        RootHash   = rootHash,
        FileCount  = 0,
        TotalSize  = 0,
        AriusVersion = "test"
    };

    private ListQueryHandler MakeHandler(FakeSeededBlobContainerService blobs, ChunkIndexService index, string account = "account", string container = "container") =>
        new(index, new FileTreeService(blobs, s_encryption, index, account, container), new SnapshotService(blobs, s_encryption, account, container), NullLogger<ListQueryHandler>.Instance, account, container);

    private static async Task<List<RepositoryEntry>> CollectAsync(IAsyncEnumerable<RepositoryEntry> source)
    {
        var list = new List<RepositoryEntry>();
        await foreach (var entry in source)
            list.Add(entry);
        return list;
    }

    private static string HashFor(string label) => Convert.ToHexString(s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label))).ToLowerInvariant();

}
