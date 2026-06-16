using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ListQueryType = Arius.Core.Features.ListQuery.ListQuery;

namespace Arius.Core.Tests.Features.ListQuery;

public class ListQueryHandlerTests
{
    private static readonly DateTimeOffset s_created = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Handle_RepositoryOnlyNonRecursive_StreamsRootDirectoryEntries()
    {
        var rootTree = Entries(
            DirectoryEntryOf("docs/", FileTreeHashOf("docs")),
            FileEntryOf("readme.txt", ContentHashOf("readme")));
        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance),
            FileCount = 1,
            TotalSize = 123,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-1", "ctr-ls-test-1", IEncryptionService.PlaintextInstance);
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("readme"), FakeChunkHash('c'), 123, 50, BlobTier.Cool));
        var handler = fixture.CreateListQueryHandler();

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);

        var directory = results.OfType<RepositoryDirectoryEntry>().Single();
        directory.RelativePath.ShouldBe(RelativePath.Parse("docs"));
        directory.State.ShouldBe(RepositoryEntryState.Repository);
        directory.TreeHash.ShouldBe(FileTreeHashOf("docs"));

        var file = results.OfType<RepositoryFileEntry>().Single();
        file.RelativePath.ShouldBe(RelativePath.Parse("readme.txt"));
        file.ContentHash.ShouldBe(ContentHashOf("readme"));
        file.OriginalSize.ShouldBe(123);
        file.Created.ShouldBe(s_created);
        file.Modified.ShouldBe(s_modified);
        file.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);
    }

    [Test]
    public async Task Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix()
    {
        var docsTree = Entries(
            DirectoryEntryOf("nested/", FakeFileTreeHash('d')),
            FileEntryOf("guide.txt", FakeContentHash('e')));

        var docsHash = FileTreeBuilder.ComputeHash(docsTree, IEncryptionService.PlaintextInstance);
        var rootTree = Entries(
            DirectoryEntryOf("docs/", docsHash),
            FileEntryOf("root.txt", FakeContentHash('f')));

        var snapshot = new SnapshotManifest
        {
            Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance),
            FileCount = 2,
            TotalSize = 456,
            AriusVersion = "test"
        };

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, docsTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-2", "ctr-ls-test-2", IEncryptionService.PlaintextInstance);
        fixture.Index.AddEntry(new ShardEntry(FakeContentHash('e'), FakeChunkHash('1'), 456, 200, BlobTier.Cool));
        var handler = fixture.CreateListQueryHandler();

        var results = new List<RepositoryEntry>();
        await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Prefix = RelativePath.Parse("docs"), Recursive = false }), CancellationToken.None))
        {
            results.Add(entry);
        }

        results.Count.ShouldBe(2);
        results.Select(entry => entry.RelativePath.ToString() + (entry is RepositoryDirectoryEntry ? "/" : string.Empty)).OrderBy(path => path).ShouldBe(["docs/guide.txt", "docs/nested/"]);
        results.ShouldNotContain(entry => entry.RelativePath == RelativePath.Parse("root.txt"));
    }

    [Test]
    public async Task Handle_Prefix_DoesNotMatchPartialSegment()
    {
        var photosTree = Entries(FileEntryOf("pic.jpg", FakeContentHash('1')));
        var photoshopTree = Entries(FileEntryOf("logo.png", FakeContentHash('2')));
        var photosHash = FileTreeBuilder.ComputeHash(photosTree, IEncryptionService.PlaintextInstance);
        var photoshopHash = FileTreeBuilder.ComputeHash(photoshopTree, IEncryptionService.PlaintextInstance);
        var rootTree = Entries(
            DirectoryEntryOf("photos/", photosHash),
            DirectoryEntryOf("photoshop/", photoshopHash));
        var snapshot = MakeSnapshot(FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance));

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, photosTree);
        await SeedTreeAsync(blobs, photoshopTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-segments", "ctr-ls-segments", IEncryptionService.PlaintextInstance);
        fixture.Index.AddEntry(new ShardEntry(FakeContentHash('1'), FakeChunkHash('3'), 10, 5, BlobTier.Cool));
        var handler = fixture.CreateListQueryHandler();

        var results = await handler.Handle(new ListQueryType(new ListQueryOptions { Prefix = RelativePath.Parse("photos"), Recursive = true }), CancellationToken.None)
            .OfType<RepositoryFileEntry>()
            .ToListAsync();

        results.Select(file => file.RelativePath).ShouldBe([RelativePath.Parse("photos/pic.jpg")]);
    }

    [Test]
    public async Task Handle_WithLocalPath_OverlaysRepositoryAndLocalFilesInOneDirectory()
    {
        var tempRoot = TestTempRoots.CreateDirectory("ls");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("shared.txt"), "local-shared", CancellationToken.None);
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("shared.txt.pointer.arius"), FakeContentHash('2').ToString(), CancellationToken.None);
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("local-only.txt"), "local-only", CancellationToken.None);

            var rootTree = Entries(
                FileEntryOf("repository-only.txt", ContentHashOf("repository-only")),
                FileEntryOf("shared.txt", ContentHashOf("shared")));
            var snapshot = new SnapshotManifest
            {
                Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
                RootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance),
                FileCount = 2,
                TotalSize = 100,
                AriusVersion = "test"
            };

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-test-3", "ctr-ls-test-3", IEncryptionService.PlaintextInstance);
            fixture.Index.AddEntry(new ShardEntry(ContentHashOf("repository-only"), FakeChunkHash('4'), 10, 5, BlobTier.Cool));
            fixture.Index.AddEntry(new ShardEntry(ContentHashOf("shared"), FakeChunkHash('5'), 20, 10, BlobTier.Cool));
            var handler = fixture.CreateListQueryHandler();

            var results = new List<RepositoryFileEntry>();
            await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { LocalPath = tempRoot.ToString(), Recursive = false }), CancellationToken.None))
            {
                if (entry is RepositoryFileEntry file)
                {
                    results.Add(file);
                }
            }

            results.Count.ShouldBe(3);

            var shared = results.Single(file => file.RelativePath == RelativePath.Parse("shared.txt"));
            shared.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated | RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary);
            shared.OriginalSize.ShouldBe(20);

            var repositoryOnly = results.Single(file => file.RelativePath == RelativePath.Parse("repository-only.txt"));
            repositoryOnly.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);
            repositoryOnly.OriginalSize.ShouldBe(10);

            var localOnly = results.Single(file => file.RelativePath == RelativePath.Parse("local-only.txt"));
            localOnly.State.ShouldBe(RepositoryEntryState.LocalBinary);
            localOnly.ContentHash.ShouldBeNull();
        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task Handle_WithPrefixAndLocalPath_PointerSuffixComparisonIsCaseInsensitive()
    {
        var tempRoot = TestTempRoots.CreateDirectory("ls-prefix-local");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Parse("docs"));

        try
        {
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("docs/shared.txt"), "local-shared", CancellationToken.None);
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("docs/shared.txt.POINTER.ARIUS"), FakeContentHash('2').ToString(), CancellationToken.None);
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("docs/local-only.txt"), "local-only", CancellationToken.None);

            var docsTree = Entries(
                FileEntryOf("repository-only.txt", ContentHashOf("repository-only")),
                FileEntryOf("shared.txt", ContentHashOf("shared")));
            var docsHash = FileTreeBuilder.ComputeHash(docsTree, IEncryptionService.PlaintextInstance);
            var rootTree = Entries(DirectoryEntryOf("docs/", docsHash));
            var snapshot = new SnapshotManifest
            {
                Timestamp = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
                RootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance),
                FileCount = 2,
                TotalSize = 100,
                AriusVersion = "test"
            };

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            await SeedTreeAsync(blobs, docsTree);
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-prefix-local", "ctr-ls-prefix-local", IEncryptionService.PlaintextInstance);
            fixture.Index.AddEntry(new ShardEntry(ContentHashOf("repository-only"), FakeChunkHash('4'), 10, 5, BlobTier.Cool));
            fixture.Index.AddEntry(new ShardEntry(ContentHashOf("shared"), FakeChunkHash('5'), 20, 10, BlobTier.Cool));
            var handler = fixture.CreateListQueryHandler();

            var results = new List<RepositoryFileEntry>();
            await foreach (var entry in handler.Handle(new ListQueryType(new ListQueryOptions { Prefix = RelativePath.Parse("docs"), LocalPath = tempRoot.ToString(), Recursive = false }), CancellationToken.None))
            {
                if (entry is RepositoryFileEntry file)
                {
                    results.Add(file);
                }
            }

            results.Count.ShouldBe(3);

            var shared = results.Single(file => file.RelativePath == RelativePath.Parse("docs/shared.txt"));
            shared.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated | RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary);

            var repositoryOnly = results.Single(file => file.RelativePath == RelativePath.Parse("docs/repository-only.txt"));
            repositoryOnly.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);

            var localOnly = results.Single(file => file.RelativePath == RelativePath.Parse("docs/local-only.txt"));
            localOnly.State.ShouldBe(RepositoryEntryState.LocalBinary);

        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public void PairFiles_PointerSuffixComparisonIsCaseInsensitive()
    {
        var files = new[]
        {
            RelativePath.Parse("docs/shared.txt"),
            RelativePath.Parse("docs/shared.txt.POINTER.ARIUS")
        };

        var result = LocalDirectoryReader.PairFiles(
            files,
            path => path.ToString() == "docs/shared.txt",
            path => path.ToString() == "docs/shared.txt" ? (12L, s_created, s_modified) : throw new InvalidOperationException($"Unexpected stat request for {path}"));

        result.Count.ShouldBe(1);

        var shared = result[PathSegment.Parse("shared.txt")];
        shared.BinaryExists.ShouldBeTrue();
        shared.PointerExists.ShouldBeTrue();
        shared.Size.ShouldBe(12);
    }

    [Test]
    public void PairFiles_DoesNotProbeForCounterpartsAlreadyPresentInEnumeratedSet()
    {
        var files = new[]
        {
            RelativePath.Parse("docs/shared.txt"),
            RelativePath.Parse("docs/shared.txt.pointer.arius"),
            RelativePath.Parse("docs/pointer-only.txt.pointer.arius")
        };

        var result = LocalDirectoryReader.PairFiles(
            files,
            path => path.ToString() switch
            {
                "docs/shared.txt.pointer.arius" => throw new InvalidOperationException($"Unexpected file existence probe for {path}"),
                _ => false
            },
            path => path.ToString() switch
            {
                "docs/shared.txt" => (12L, s_created, s_modified),
                _ => throw new InvalidOperationException($"Unexpected stat request for {path}")
            });

        result.Count.ShouldBe(2);
        result[PathSegment.Parse("shared.txt")].PointerExists.ShouldBeTrue();
        result[PathSegment.Parse("pointer-only.txt")].BinaryExists.ShouldBeFalse();
        result[PathSegment.Parse("shared.txt")].Size.ShouldBe(12);
        result[PathSegment.Parse("pointer-only.txt")].Size.ShouldBeNull();
    }

    [Test]
    public async Task Handle_RecursiveLocalOnlyDirectory_DescendsAndYieldsLocalFiles()
    {
        var tempRoot = TestTempRoots.CreateDirectory("ls-local-recursive");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Parse("local-only-dir"));

        try
        {
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("local-only-dir/nested.txt"), "nested", CancellationToken.None);

            IReadOnlyList<FileTreeEntry> rootTree = [];
            var snapshot = MakeSnapshot(FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance));

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-local-recursive", "ctr-ls-local-recursive", IEncryptionService.PlaintextInstance);
            var handler = fixture.CreateListQueryHandler();

            var results = await handler.Handle(new ListQueryType(new ListQueryOptions { LocalPath = tempRoot.ToString(), Recursive = true }), CancellationToken.None)
                .ToListAsync();

            var directory = results.OfType<RepositoryDirectoryEntry>().Single();
            directory.RelativePath.ShouldBe(RelativePath.Parse("local-only-dir"));
            directory.State.ShouldBe(RepositoryEntryState.LocalDirectory);
            directory.TreeHash.ShouldBeNull();

            var file = results.OfType<RepositoryFileEntry>().Single();
            file.RelativePath.ShouldBe(RelativePath.Parse("local-only-dir/nested.txt"));
            file.State.ShouldBe(RepositoryEntryState.LocalBinary);
            file.ContentHash.ShouldBeNull();
            file.OriginalSize.ShouldBe(6);
        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public void LocalDirectoryReader_Read_MissingDirectory_ReturnsEmptyListingAndLogsWarnings()
    {
        var tempRoot = TestTempRoots.CreateDirectory("ls-missing-local-dir");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            var logger = new FakeLogger<ListQueryHandler>();

            var listing = LocalDirectoryReader.Read(localFileSystem, RelativePath.Parse("missing"), logger);

            listing.Files.ShouldBeEmpty();
            listing.Subdirectories.ShouldBeEmpty();

            var warnings = logger.Collector.GetSnapshot()
                .Where(record => record.Level == LogLevel.Warning)
                .ToList();
            warnings.Count.ShouldBe(2);
            warnings.ShouldContain(record => record.Message.Contains("Could not enumerate subdirectories"));
            warnings.ShouldContain(record => record.Message.Contains("Could not enumerate files"));
        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService("ls");
        var fileTreeService = new FileTreeService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance, "acct-ls-missing", "ctr-ls-missing");
        var snapshotSvc = new SnapshotService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance, "acct-ls-missing", "ctr-ls-missing");
        using var index = new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance, snapshotSvc, "acct-ls-missing", "ctr-ls-missing");
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
        var childTree = Entries(FileEntryOf("deep.txt", FakeContentHash('6')));
        var childHash = FileTreeBuilder.ComputeHash(childTree, IEncryptionService.PlaintextInstance);
        var rootTree = Entries(
            DirectoryEntryOf("child/", childHash),
            FileEntryOf("root.txt", FakeContentHash('7')));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, childTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-33-nr", "ctr-33-nr", IEncryptionService.PlaintextInstance);
        var handler = fixture.CreateListQueryHandler();

        var nonRecursive = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None).ToListAsync();
        nonRecursive.Count.ShouldBe(2);
        nonRecursive.ShouldContain(e => e.RelativePath == RelativePath.Parse("child"));
        nonRecursive.ShouldContain(e => e.RelativePath == RelativePath.Parse("root.txt"));
        nonRecursive.ShouldNotContain(e => e.RelativePath == RelativePath.Parse("child/deep.txt"));

        await using var fixture2 = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-33-r", "ctr-33-r", IEncryptionService.PlaintextInstance);
        var handler2 = fixture2.CreateListQueryHandler();

        var recursive = await handler2.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None).ToListAsync();
        recursive.ShouldContain(e => e.RelativePath == RelativePath.Parse("child"));
        recursive.ShouldContain(e => e.RelativePath == RelativePath.Parse("root.txt"));
        recursive.ShouldContain(e => e.RelativePath == RelativePath.Parse("child/deep.txt"));
    }

    [Test]
    public async Task Handle_FilenameFilter_CaseInsensitiveMatchOnFilesNotDirs()
    {
        var rootTree = Entries(
            DirectoryEntryOf("Photos/", FakeFileTreeHash('8')),
            FileEntryOf("VACATION.jpg", FakeContentHash('9')),
            FileEntryOf("sunset.jpg", FakeContentHash('a')),
            FileEntryOf("readme.txt", FakeContentHash('c')));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-36", "ctr-36", IEncryptionService.PlaintextInstance);
        var handler = fixture.CreateListQueryHandler();

        // Filter "vacation" should match VACATION.jpg (case-insensitive), not sunset or readme
        var results = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, Filter = "vacation" }), CancellationToken.None).ToListAsync();

         // Directories are NOT filtered — Photos/ should still appear
        results.ShouldContain(e => e.RelativePath == RelativePath.Parse("Photos"));
        results.ShouldContain(e => e.RelativePath == RelativePath.Parse("VACATION.jpg"));
        results.ShouldNotContain(e => e.RelativePath == RelativePath.Parse("sunset.jpg"));
        results.ShouldNotContain(e => e.RelativePath == RelativePath.Parse("readme.txt"));
    }

    [Test]
    public async Task Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags()
    {
        var tempRoot = TestTempRoots.CreateDirectory("ls-38");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Parse("repository-local-dir"));
        localFileSystem.CreateDirectory(RelativePath.Parse("local-only-dir"));

        try
        {
            IReadOnlyList<FileTreeEntry> repositoryLocalTree = [];
            IReadOnlyList<FileTreeEntry> repositoryOnlyTree = [];
            var repositoryLocalHash = FileTreeBuilder.ComputeHash(repositoryLocalTree, IEncryptionService.PlaintextInstance);
            var repositoryOnlyHash = FileTreeBuilder.ComputeHash(repositoryOnlyTree, IEncryptionService.PlaintextInstance);

            // root has: repository+local dir, repository-only dir; local has: local-only dir
            var rootTree = Entries(
                DirectoryEntryOf("repository-local-dir/", repositoryLocalHash),
                DirectoryEntryOf("repository-only-dir/", repositoryOnlyHash));
            var rootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance);
            var snapshot = MakeSnapshot(rootHash);

            var blobs = new FakeSeededBlobContainerService();
            await SeedTreeAsync(blobs, rootTree);
            await SeedTreeAsync(blobs, repositoryLocalTree);
            await SeedTreeAsync(blobs, repositoryOnlyTree);
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-38", "ctr-38", IEncryptionService.PlaintextInstance);
            var handler = fixture.CreateListQueryHandler();

            var dirs = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false, LocalPath = tempRoot.ToString() }), CancellationToken.None)
                .OfType<RepositoryDirectoryEntry>()
                .ToListAsync();

            dirs.Count.ShouldBe(3);

            var repositoryLocal = dirs.Single(d => d.RelativePath == RelativePath.Parse("repository-local-dir"));
            repositoryLocal.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.LocalDirectory);

            var repositoryOnly = dirs.Single(d => d.RelativePath == RelativePath.Parse("repository-only-dir"));
            repositoryOnly.State.ShouldBe(RepositoryEntryState.Repository);

            var localOnly = dirs.Single(d => d.RelativePath == RelativePath.Parse("local-only-dir"));
            localOnly.State.ShouldBe(RepositoryEntryState.LocalDirectory);
        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task Handle_SizeLookup_SizeNullAndBareRepositoryStateWhenNotInIndex()
    {
        var childTree = Entries(FileEntryOf("child-file.txt", FakeContentHash('d')));
        var childHash = FileTreeBuilder.ComputeHash(childTree, IEncryptionService.PlaintextInstance);
        var rootTree = Entries(
            DirectoryEntryOf("child/", childHash),
            FileEntryOf("known.txt", ContentHashOf("known")),
            FileEntryOf("unknown.txt", FakeContentHash('f')));
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        await SeedTreeAsync(blobs, childTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-39", "ctr-39", IEncryptionService.PlaintextInstance);
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("known"), FakeChunkHash('b'), 999, 500, BlobTier.Cool));

        var handler = fixture.CreateListQueryHandler();
        var files   = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = true }), CancellationToken.None)
            .OfType<RepositoryFileEntry>()
            .ToListAsync();

        var known   = files.Single(f => f.RelativePath == RelativePath.Parse("known.txt"));
        var unknown = files.Single(f => f.RelativePath == RelativePath.Parse("unknown.txt"));
        var child   = files.Single(f => f.RelativePath == RelativePath.Parse("child/child-file.txt"));

        known.OriginalSize.ShouldBe(999);
        known.State.ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);

        // Not in the chunk index: present in the tree but no size and no tier refinement.
        unknown.OriginalSize.ShouldBeNull();
        unknown.State.ShouldBe(RepositoryEntryState.Repository);

        child.OriginalSize.ShouldBeNull();
        child.State.ShouldBe(RepositoryEntryState.Repository);
    }

    [Test]
    public async Task Handle_StorageTierHint_MapsToHydratedOrArchivedState()
    {
        var rootTree = Entries(
            FileEntryOf("hot.txt", ContentHashOf("hot")),
            FileEntryOf("cool.txt", ContentHashOf("cool")),
            FileEntryOf("archived.txt", ContentHashOf("archived")));
        var snapshot = MakeSnapshot(FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance));

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-ls-tier", "ctr-ls-tier", IEncryptionService.PlaintextInstance);
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("hot"), FakeChunkHash('1'), 10, 5, BlobTier.Hot));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("cool"), FakeChunkHash('2'), 20, 10, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("archived"), FakeChunkHash('3'), 30, 15, BlobTier.Archive));
        var handler = fixture.CreateListQueryHandler();

        var files = await handler.Handle(new ListQueryType(new ListQueryOptions { Recursive = false }), CancellationToken.None)
            .OfType<RepositoryFileEntry>()
            .ToListAsync();

        files.Single(f => f.RelativePath == RelativePath.Parse("hot.txt")).State
            .ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);
        files.Single(f => f.RelativePath == RelativePath.Parse("cool.txt")).State
            .ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryHydrated);
        files.Single(f => f.RelativePath == RelativePath.Parse("archived.txt")).State
            .ShouldBe(RepositoryEntryState.Repository | RepositoryEntryState.RepositoryArchived);
    }

    [Test]
    public async Task Handle_NoSnapshots_ThrowsInvalidOperationException()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-310", "ctr-310", IEncryptionService.PlaintextInstance);
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
        var rootHash = FileTreeBuilder.ComputeHash(rootTree, IEncryptionService.PlaintextInstance);
        var snapshot = MakeSnapshot(rootHash);

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, rootTree);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-310b", "ctr-310b", IEncryptionService.PlaintextInstance);
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
        var leafHash = FileTreeBuilder.ComputeHash(leafTree, IEncryptionService.PlaintextInstance);

        // Build chain: level10 → level9 → … → level1 → root
        var currentHash  = leafHash;
        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, leafTree);

        for (var i = 10; i >= 1; i--)
        {
            var tree = Entries(
                DirectoryEntryOf($"level{i + 1}/", currentHash),
                FileEntryOf($"file{i}.txt", FakeContentHash("123456789a"[10 - i])));
            currentHash = FileTreeBuilder.ComputeHash(tree, IEncryptionService.PlaintextInstance);
            await SeedTreeAsync(blobs, tree);
        }

        var snapshot = MakeSnapshot(currentHash);
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, TestCompression.Instance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-311", "ctr-311", IEncryptionService.PlaintextInstance);
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

    private static IReadOnlyList<FileTreeEntry> Entries(params FileTreeEntry[] entries) => entries;

    private static async Task<FileTreeHash> SeedTreeAsync(FakeSeededBlobContainerService blobs, IReadOnlyList<FileTreeEntry> entries)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload = (Hash: FileTreeHashOf(plaintext), Plaintext: (ReadOnlyMemory<byte>)plaintext);
        using var ms = new MemoryStream();

        await using (var encStream = IEncryptionService.PlaintextInstance.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzipStream.WriteAsync(payload.Plaintext);
        }

        blobs.AddBlob(BlobPaths.FileTreePath(payload.Hash), ms.ToArray());
        return payload.Hash;
    }

    private static FileEntry FileEntryOf(string name, ContentHash hash) => new()
    {
        Name = PathSegment.Parse(name),
        ContentHash = hash,
        Created = s_created,
        Modified = s_modified
    };

    private static DirectoryEntry DirectoryEntryOf(string name, FileTreeHash hash) => new()
    {
        Name = PathSegment.Parse(name.TrimEnd('/')),
        FileTreeHash = hash
    };
}
