using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Arius.Core.Tests.Ls;

public class ListHandlerTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();
    private static readonly DateTimeOffset s_created = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Handle_CloudOnlyNonRecursive_StreamsRootDirectoryEntries()
    {
        var rootTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "docs/", Type = TreeEntryType.Dir, Hash = HashFor("docs") },
                new TreeEntry { Name = "readme.txt", Type = TreeEntryType.File, Hash = HashFor("readme"), Created = s_created, Modified = s_modified }
            ]
        };

        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = rootHash,
            FileCount = 1,
            TotalSize = 123,
            AriusVersion = "test"
        };

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-1", "ctr-ls-test-1", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("readme"), HashFor("chunk"), 123, 50));

        var handler = new ListQueryHandler(
            blobs,
            s_encryption,
            index,
            NullLogger<ListQueryHandler>.Instance,
            "account",
            "container");

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQuery(new ListQueryOptions { Recursive = false }), CancellationToken.None))
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
        var docsTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "nested/", Type = TreeEntryType.Dir, Hash = HashFor("nested") },
                new TreeEntry { Name = "guide.txt", Type = TreeEntryType.File, Hash = HashFor("guide"), Created = s_created, Modified = s_modified }
            ]
        };

        var docsHash = TreeBlobSerializer.ComputeHash(docsTree, s_encryption);
        var rootTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "docs/", Type = TreeEntryType.Dir, Hash = docsHash },
                new TreeEntry { Name = "root.txt", Type = TreeEntryType.File, Hash = HashFor("root"), Created = s_created, Modified = s_modified }
            ]
        };

        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = rootHash,
            FileCount = 2,
            TotalSize = 456,
            AriusVersion = "test"
        };

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(docsHash), await TreeBlobSerializer.SerializeForStorageAsync(docsTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-2", "ctr-ls-test-2", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("guide"), HashFor("chunk-guide"), 456, 200));

        var handler = new ListQueryHandler(
            blobs,
            s_encryption,
            index,
            NullLogger<ListQueryHandler>.Instance,
            "account",
            "container");

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQuery(new ListQueryOptions { Prefix = "docs", Recursive = false }), CancellationToken.None))
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

            var rootTree = new TreeBlob
            {
                Entries =
                [
                    new TreeEntry { Name = "cloud-only.txt", Type = TreeEntryType.File, Hash = HashFor("cloud-only"), Created = s_created, Modified = s_modified },
                    new TreeEntry { Name = "shared.txt", Type = TreeEntryType.File, Hash = HashFor("shared"), Created = s_created, Modified = s_modified }
                ]
            };

            var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
            var snapshot = new SnapshotManifest
            {
                Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
                RootHash = rootHash,
                FileCount = 2,
                TotalSize = 100,
                AriusVersion = "test"
            };

            var blobs = new FakeBlobService();
            blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            using var index = new ChunkIndexService(blobs, s_encryption, "acct-ls-test-3", "ctr-ls-test-3", cacheBudgetBytes: 1024 * 1024);
            index.RecordEntry(new ShardEntry(HashFor("cloud-only"), HashFor("chunk-cloud"), 10, 5));
            index.RecordEntry(new ShardEntry(HashFor("shared"), HashFor("chunk-shared"), 20, 10));

            var handler = new ListQueryHandler(
                blobs,
                s_encryption,
                index,
                NullLogger<ListQueryHandler>.Instance,
                "account",
                "container");

            var results = new List<RepositoryFileEntry>();
            await foreach (var entry in handler.Handle(new ListQuery(new ListQueryOptions { LocalPath = tempRoot, Recursive = false }), CancellationToken.None))
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

    // ── 3.3: recursive vs non-recursive ──────────────────────────────────────

    [Test]
    public async Task Handle_RecursiveFalse_YieldsOnlyImmediateChildren()
    {
        var childTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "deep.txt", Type = TreeEntryType.File, Hash = HashFor("deep"), Created = s_created, Modified = s_modified }
            ]
        };
        var childHash = TreeBlobSerializer.ComputeHash(childTree, s_encryption);
        var rootTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "child/", Type = TreeEntryType.Dir, Hash = childHash },
                new TreeEntry { Name = "root.txt", Type = TreeEntryType.File, Hash = HashFor("root"), Created = s_created, Modified = s_modified }
            ]
        };
        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(childHash), await TreeBlobSerializer.SerializeForStorageAsync(childTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-33-nr", "ctr-33-nr", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var nonRecursive = await CollectAsync(handler.Handle(new ListQuery(new ListQueryOptions { Recursive = false }), CancellationToken.None));
        nonRecursive.Count.ShouldBe(2);
        nonRecursive.ShouldContain(e => e.RelativePath == "child/");
        nonRecursive.ShouldContain(e => e.RelativePath == "root.txt");
        nonRecursive.ShouldNotContain(e => e.RelativePath == "child/deep.txt");

        using var index2 = new ChunkIndexService(blobs, s_encryption, "acct-33-r", "ctr-33-r", cacheBudgetBytes: 1024 * 1024);
        var handler2 = MakeHandler(blobs, index2);

        var recursive = await CollectAsync(handler2.Handle(new ListQuery(new ListQueryOptions { Recursive = true }), CancellationToken.None));
        recursive.ShouldContain(e => e.RelativePath == "child/");
        recursive.ShouldContain(e => e.RelativePath == "root.txt");
        recursive.ShouldContain(e => e.RelativePath == "child/deep.txt");
    }

    // ── 3.6: filename substring filter ───────────────────────────────────────

    [Test]
    public async Task Handle_FilenameFilter_CaseInsensitiveMatchOnFilesNotDirs()
    {
        var rootTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "Photos/", Type = TreeEntryType.Dir, Hash = HashFor("photos-dir") },
                new TreeEntry { Name = "VACATION.jpg", Type = TreeEntryType.File, Hash = HashFor("vac"), Created = s_created, Modified = s_modified },
                new TreeEntry { Name = "sunset.jpg",   Type = TreeEntryType.File, Hash = HashFor("sun"), Created = s_created, Modified = s_modified },
                new TreeEntry { Name = "readme.txt",   Type = TreeEntryType.File, Hash = HashFor("rdm"), Created = s_created, Modified = s_modified },
            ]
        };
        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-36", "ctr-36", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        // Filter "vacation" should match VACATION.jpg (case-insensitive), not sunset or readme
        var results = await CollectAsync(handler.Handle(new ListQuery(new ListQueryOptions { Recursive = false, Filter = "vacation" }), CancellationToken.None));

        // Directories are NOT filtered — Photos/ should still appear
        results.ShouldContain(e => e.RelativePath == "Photos/");
        results.ShouldContain(e => e.RelativePath == "VACATION.jpg");
        results.ShouldNotContain(e => e.RelativePath == "sunset.jpg");
        results.ShouldNotContain(e => e.RelativePath == "readme.txt");
    }

    // ── 3.8: directory merge flags ────────────────────────────────────────────

    [Test]
    public async Task Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-ls-38-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "cloud-local-dir"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "local-only-dir"));

        try
        {
            var cloudLocalTree  = new TreeBlob { Entries = [] };
            var cloudOnlyTree   = new TreeBlob { Entries = [] };
            var cloudLocalHash  = TreeBlobSerializer.ComputeHash(cloudLocalTree, s_encryption);
            var cloudOnlyHash   = TreeBlobSerializer.ComputeHash(cloudOnlyTree, s_encryption);

            // root has: cloud+local dir, cloud-only dir; local has: local-only dir
            var rootTree = new TreeBlob
            {
                Entries =
                [
                    new TreeEntry { Name = "cloud-local-dir/", Type = TreeEntryType.Dir, Hash = cloudLocalHash },
                    new TreeEntry { Name = "cloud-only-dir/",  Type = TreeEntryType.Dir, Hash = cloudOnlyHash },
                ]
            };
            var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
            var snapshot = MakeSnapshot(rootHash);

            var blobs = new FakeBlobService();
            blobs.AddBlob(BlobPaths.FileTree(rootHash),       await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
            blobs.AddBlob(BlobPaths.FileTree(cloudLocalHash), await TreeBlobSerializer.SerializeForStorageAsync(cloudLocalTree, s_encryption));
            blobs.AddBlob(BlobPaths.FileTree(cloudOnlyHash),  await TreeBlobSerializer.SerializeForStorageAsync(cloudOnlyTree, s_encryption));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

            using var index = new ChunkIndexService(blobs, s_encryption, "acct-38", "ctr-38", cacheBudgetBytes: 1024 * 1024);
            var handler = MakeHandler(blobs, index);

            var results = await CollectAsync(handler.Handle(new ListQuery(new ListQueryOptions { Recursive = false, LocalPath = tempRoot }), CancellationToken.None));
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

    // ── 3.9: per-directory batch size lookup ──────────────────────────────────

    [Test]
    public async Task Handle_BatchSizeLookup_CalledOncePerDirectory_SizeNullWhenNotInIndex()
    {
        var childTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "child-file.txt", Type = TreeEntryType.File, Hash = HashFor("child-file"), Created = s_created, Modified = s_modified }
            ]
        };
        var childHash = TreeBlobSerializer.ComputeHash(childTree, s_encryption);
        var rootTree = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "child/",    Type = TreeEntryType.Dir,  Hash = childHash },
                new TreeEntry { Name = "known.txt", Type = TreeEntryType.File, Hash = HashFor("known"),   Created = s_created, Modified = s_modified },
                new TreeEntry { Name = "unknown.txt",Type = TreeEntryType.File, Hash = HashFor("unknown"), Created = s_created, Modified = s_modified },
            ]
        };
        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash),  await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(BlobPaths.FileTree(childHash), await TreeBlobSerializer.SerializeForStorageAsync(childTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        // Only register "known.txt" — "unknown.txt" and "child-file.txt" have no size entry
        using var index = new ChunkIndexService(blobs, s_encryption, "acct-39", "ctr-39", cacheBudgetBytes: 1024 * 1024);
        index.RecordEntry(new ShardEntry(HashFor("known"), HashFor("chunk-known"), 999, 500));

        var handler = MakeHandler(blobs, index);
        var results = await CollectAsync(handler.Handle(new ListQuery(new ListQueryOptions { Recursive = true }), CancellationToken.None));
        var files = results.OfType<RepositoryFileEntry>().ToList();

        var known   = files.Single(f => f.RelativePath == "known.txt");
        var unknown = files.Single(f => f.RelativePath == "unknown.txt");
        var child   = files.Single(f => f.RelativePath == "child/child-file.txt");

        known.OriginalSize.ShouldBe(999);
        unknown.OriginalSize.ShouldBeNull();
        child.OriginalSize.ShouldBeNull();
    }

    // ── 3.10: snapshot not found ──────────────────────────────────────────────

    [Test]
    public async Task Handle_NoSnapshots_ThrowsInvalidOperationException()
    {
        var blobs = new FakeBlobService(); // empty — no snapshot blob
        using var index = new ChunkIndexService(blobs, s_encryption, "acct-310", "ctr-310", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQuery(new ListQueryOptions()), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("snapshot", Case.Insensitive);
    }

    [Test]
    public async Task Handle_SpecificVersionNotFound_ThrowsWithDescriptiveMessage()
    {
        var rootTree = new TreeBlob { Entries = [] };
        var rootHash = TreeBlobSerializer.ComputeHash(rootTree, s_encryption);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(rootHash), await TreeBlobSerializer.SerializeForStorageAsync(rootTree, s_encryption));
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-310b", "ctr-310b", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new ListQuery(new ListQueryOptions { Version = "9999-not-found" }), CancellationToken.None)) { }
        });
        ex.Message.ShouldContain("9999-not-found");
    }

    // ── 3.11: cancellation ────────────────────────────────────────────────────

    [Test]
    public async Task Handle_CancellationRequested_StopsEnumeration()
    {
        // Build a deep tree: root → dir1 → dir2 → ... → dir10.
        // Each level is a separate WalkDirectoryAsync call, so cancellation is
        // checked at each level boundary (ThrowIfCancellationRequested at top of method).
        var leafTree = new TreeBlob { Entries = [] };
        var leafHash = TreeBlobSerializer.ComputeHash(leafTree, s_encryption);

        // Build chain: level10 → level9 → … → level1 → root
        var currentHash  = leafHash;
        var blobs = new FakeBlobService();
        blobs.AddBlob(BlobPaths.FileTree(leafHash), await TreeBlobSerializer.SerializeForStorageAsync(leafTree, s_encryption));

        for (var i = 10; i >= 1; i--)
        {
            var tree = new TreeBlob
            {
                Entries =
                [
                    new TreeEntry { Name = $"level{i + 1}/", Type = TreeEntryType.Dir, Hash = currentHash },
                    new TreeEntry { Name = $"file{i}.txt",   Type = TreeEntryType.File, Hash = HashFor($"f{i}"), Created = s_created, Modified = s_modified }
                ]
            };
            currentHash = TreeBlobSerializer.ComputeHash(tree, s_encryption);
            blobs.AddBlob(BlobPaths.FileTree(currentHash), await TreeBlobSerializer.SerializeForStorageAsync(tree, s_encryption));
        }

        var snapshot = MakeSnapshot(currentHash);
        blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, s_encryption));

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-311", "ctr-311", cacheBudgetBytes: 1024 * 1024);
        var handler = MakeHandler(blobs, index);

        using var cts = new CancellationTokenSource();
        var collected = new List<RepositoryEntry>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in handler.Handle(new ListQuery(new ListQueryOptions { Recursive = true }), cts.Token))
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

    private static SnapshotManifest MakeSnapshot(string rootHash) => new()
    {
        Timestamp  = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
        RootHash   = rootHash,
        FileCount  = 0,
        TotalSize  = 0,
        AriusVersion = "test"
    };

    private ListQueryHandler MakeHandler(FakeBlobService blobs, ChunkIndexService index) =>
        new(blobs, s_encryption, index, NullLogger<ListQueryHandler>.Instance, "account", "container");

    private static async Task<List<RepositoryEntry>> CollectAsync(IAsyncEnumerable<RepositoryEntry> source)
    {
        var list = new List<RepositoryEntry>();
        await foreach (var entry in source)
            list.Add(entry);
        return list;
    }

    private static string HashFor(string label) => Convert.ToHexString(s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label))).ToLowerInvariant();

    private sealed class FakeBlobService : IBlobContainerService
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public void AddBlob(string blobName, byte[] content) => _blobs[blobName] = content;

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
        {
            if (!_blobs.TryGetValue(blobName, out var content))
            {
                throw new FileNotFoundException(blobName);
            }

            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlobMetadata { Exists = _blobs.ContainsKey(blobName) });

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var name in _blobs.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
                await Task.Yield();
            }
        }

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
