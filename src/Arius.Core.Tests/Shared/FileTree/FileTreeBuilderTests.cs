using System.Runtime.CompilerServices;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.FileTree.Fakes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBuilderTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    private static async Task<(FileTreeStagingSession Session, string StagingRoot)> CreateStagingAsync(
        string accountName,
        string containerName,
        params (string Path, ContentHash Hash, DateTimeOffset Created, DateTimeOffset Modified)[] files)
    {
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        var session = await FileTreeStagingSession.OpenAsync(cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);
        foreach (var file in files)
            await writer.AppendFileEntryAsync(RelativePath.Parse(file.Path), file.Hash, file.Created, file.Modified);

        return (session, session.StagingRoot);
    }

    private static FileTreeBuilder CreateBuilder(
        IBlobContainerService blobs,
        string accountName,
        string containerName,
        out FileTreeService fileTreeService)
    {
        var index = new ChunkIndexService(blobs, s_enc, accountName, containerName);
        fileTreeService = new FileTreeService(blobs, s_enc, index, accountName, containerName);
        return new FileTreeBuilder(s_enc, fileTreeService);
    }

    private static async Task WriteNodeLinesAsync(string stagingRoot, string directoryId, params string[] lines)
    {
        await File.WriteAllLinesAsync(FileTreePaths.GetStagingNodePath(stagingRoot, directoryId), lines);
    }

    [Test]
    public async Task SynchronizeAsync_EmptyManifest_ReturnsNull()
    {
        const string accountName = "account-empty";
        const string containerName = "container-empty";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var blobs   = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();
            await using var stagingSession = await FileTreeStagingSession.OpenAsync(cacheDir);
            var root = await builder.SynchronizeAsync(stagingSession.StagingRoot);

            root.ShouldBeNull();
            blobs.Uploaded.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_SingleFile_RootTreeUploaded()
    {
        const string acct = "acct-single";
        const string cont = "cont-single";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(acct, cont);

        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var (stagingSession, stagingRoot) = await CreateStagingAsync(acct, cont, ("readme.txt", FakeContentHash('b'), now, now));

            var blobs   = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, acct, cont, out var fileTreeService);
            await fileTreeService.ValidateAsync();
            await using (stagingSession)
            {
                var root = await builder.SynchronizeAsync(stagingRoot);

                root.ShouldNotBeNull();
                blobs.Uploaded.Count.ShouldBeGreaterThanOrEqualTo(1);
            }
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_IdenticalManifest_SameRootHash()
    {
        const string acct1 = "acc-identical-1", cont1 = "con-identical-1";
        const string acct2 = "acc-identical-2", cont2 = "con-identical-2";
        var cache1 = RepositoryPaths.GetFileTreeCacheDirectory(acct1, cont1);
        var cache2 = RepositoryPaths.GetFileTreeCacheDirectory(acct2, cont2);
        if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
        if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);

        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession1 = (await CreateStagingAsync(
                acct1,
                cont1,
                ("photos/a.jpg", FakeContentHash('c'), now, now),
                ("photos/b.jpg", FakeContentHash('d'), now, now),
                ("docs/r.pdf", FakeContentHash('e'), now, now))).Session;
            var stagingRoot1 = stagingSession1.StagingRoot;

            await using var stagingSession2 = (await CreateStagingAsync(
                acct2,
                cont2,
                ("photos/a.jpg", FakeContentHash('c'), now, now),
                ("photos/b.jpg", FakeContentHash('d'), now, now),
                ("docs/r.pdf", FakeContentHash('e'), now, now))).Session;
            var stagingRoot2 = stagingSession2.StagingRoot;

            var blobs1   = new FakeRecordingBlobContainerService();
            var blobs2   = new FakeRecordingBlobContainerService();
            var builder1 = CreateBuilder(blobs1, acct1, cont1, out var fileTreeService1);
            var builder2 = CreateBuilder(blobs2, acct2, cont2, out var fileTreeService2);
            await fileTreeService1.ValidateAsync();
            await fileTreeService2.ValidateAsync();
            var root1    = await builder1.SynchronizeAsync(stagingRoot1);
            var root2    = await builder2.SynchronizeAsync(stagingRoot2);

            root1.ShouldBe(root2);
        }
        finally
        {
            if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
            if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_MetadataChange_DifferentRootHash()
    {
        const string acct = "acc-meta", cont = "con-meta";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(acct, cont);
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now1  = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var now2  = new DateTimeOffset(2025, 1, 1,  0,  0, 0, TimeSpan.Zero);

            var blobs1 = new FakeRecordingBlobContainerService();
            var blobs2 = new FakeRecordingBlobContainerService();
            FileTreeHash? root1;
            await using (var stagingSession1 = (await CreateStagingAsync(acct, cont, ("file.txt", FakeContentHash('f'), now1, now1))).Session)
            {
                var builder1 = CreateBuilder(blobs1, acct, cont, out var fileTreeService1);
                await fileTreeService1.ValidateAsync();
                root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);
            }

            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

            FileTreeHash? root2;
            await using (var stagingSession2 = (await CreateStagingAsync(acct, cont, ("file.txt", FakeContentHash('f'), now1, now2))).Session)
            {
                var builder2 = CreateBuilder(blobs2, acct, cont, out var fileTreeService2);
                await fileTreeService2.ValidateAsync();
                root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
            }

            root1.ShouldNotBe(root2);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_DuplicateFileNamesInOneDirectory_Throws()
    {
        const string accountName = "acc-dup-file";
        const string containerName = "con-dup-file";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
            var first = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
            {
                Name = SegmentOf("a.txt"),
                ContentHash = FakeContentHash('a'),
                Created = now,
                Modified = now
            });
            var second = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
            {
                Name = SegmentOf("a.txt"),
                ContentHash = FakeContentHash('b'),
                Created = now,
                Modified = now
            });

            await using var stagingSession = await FileTreeStagingSession.OpenAsync(cacheDir);
            await WriteNodeLinesAsync(stagingSession.StagingRoot, rootId, first, second);

            var blobs = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            await Should.ThrowAsync<InvalidOperationException>(() => builder.SynchronizeAsync(stagingSession.StagingRoot));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task ReadNodeEntriesAsync_DuplicateFileDetectedBeforeSourceIsFullyEnumerated()
    {
        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var enumerationAdvancedPastDuplicate = false;
        var first = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = SegmentOf("a.txt"),
            ContentHash = FakeContentHash('a'),
            Created = now,
            Modified = now
        });
        var duplicate = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = SegmentOf("a.txt"),
            ContentHash = FakeContentHash('b'),
            Created = now,
            Modified = now
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FileTreeBuilder.ReadNodeEntriesAsync(EnumerateLines(), CancellationToken.None));

        ex.ShouldNotBeNull();
        ex.Message.ShouldContain("Duplicate staged file entry 'a.txt'.");
        enumerationAdvancedPastDuplicate.ShouldBeFalse();

        async IAsyncEnumerable<string> EnumerateLines([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return first;
            await Task.Yield();

            cancellationToken.ThrowIfCancellationRequested();
            yield return duplicate;
            await Task.Yield();

            enumerationAdvancedPastDuplicate = true;
            throw new InvalidOperationException("The parser should stop once it finds the duplicate.");
        }
    }

    [Test]
    public async Task SynchronizeAsync_IdenticalDuplicateDirectoryEntries_SameRootHash()
    {
        const string accountName = "acc-dup-dir";
        const string containerName = "con-dup-dir";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var childId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
            var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);

            await using var stagingSession1 = await FileTreeStagingSession.OpenAsync(cacheDir);
            await WriteNodeLinesAsync(
                stagingSession1.StagingRoot,
                rootId,
                $"{childId} D photos",
                $"{childId} D photos");
            await WriteNodeLinesAsync(
                stagingSession1.StagingRoot,
                childId,
                FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
                {
                    Name = SegmentOf("a.jpg"),
                    ContentHash = FakeContentHash('c'),
                    Created = now,
                    Modified = now
                }));

            var blobs1 = new FakeRecordingBlobContainerService();
            var builder1 = CreateBuilder(blobs1, accountName, containerName, out var fileTreeService1);
            await fileTreeService1.ValidateAsync();
            var root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);

            await stagingSession1.DisposeAsync();
            Directory.Delete(cacheDir, recursive: true);

            await using var stagingSession2 = await FileTreeStagingSession.OpenAsync(cacheDir);
            await WriteNodeLinesAsync(
                stagingSession2.StagingRoot,
                rootId,
                $"{childId} D photos");
            await WriteNodeLinesAsync(
                stagingSession2.StagingRoot,
                childId,
                FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
                {
                    Name = SegmentOf("a.jpg"),
                    ContentHash = FakeContentHash('c'),
                    Created = now,
                    Modified = now
                }));

            var blobs2 = new FakeRecordingBlobContainerService();
            var builder2 = CreateBuilder(blobs2, accountName, containerName, out var fileTreeService2);
            await fileTreeService2.ValidateAsync();
            var root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);

            root1.ShouldBe(root2);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_CalculatesSiblingNodes_WhileUploadsAreBlocked()
    {
        const string accountName = "acc-blocked-uploads";
        const string containerName = "con-blocked-uploads";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = (await CreateStagingAsync(
                accountName,
                containerName,
                ("photos/a.jpg", FakeContentHash('d'), now, now),
                ("docs/b.jpg", FakeContentHash('e'), now, now))).Session;

            var blobs = new BlockingFileTreeUploadBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            var syncTask = builder.SynchronizeAsync(stagingSession.StagingRoot);

            (await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
            blobs.AllowUploads();

            (await syncTask).ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_DeduplicatesBlob_WhenAlreadyOnDisk()
    {
        const string accountName = "acc";
        const string containerName = "con";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now   = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var blobs   = new FakeRecordingBlobContainerService();
            FileTreeHash? root;
            await using (var stagingSession1 = (await CreateStagingAsync(accountName, containerName, ("file.txt", FakeContentHash('1'), now, now))).Session)
            {
                var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService1);
                await fileTreeService1.ValidateAsync();
                root = await builder.SynchronizeAsync(stagingSession1.StagingRoot);
            }

            var uploadCount1 = blobs.Uploaded.Count;
            uploadCount1.ShouldBeGreaterThan(0);

            var blobs2   = new FakeRecordingBlobContainerService();
            foreach (var blobName in blobs.Uploaded)
                blobs2.SeedRemoteBlob(blobName);

            FileTreeHash? root2;
            await using (var stagingSession2 = (await CreateStagingAsync(accountName, containerName, ("file.txt", FakeContentHash('1'), now, now))).Session)
            {
                var builder2 = CreateBuilder(blobs2, accountName, containerName, out var fileTreeService2);
                await fileTreeService2.ValidateAsync();
                root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
            }

            root2.ShouldBe(root);
            blobs2.Uploaded.Count.ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_WithoutValidation_FailsFastBeforeUpload()
    {
        const string accountName = "acc-unvalidated";
        const string containerName = "con-unvalidated";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var stagingSession = (await CreateStagingAsync(accountName, containerName, ("file.txt", FakeContentHash('2'), now, now))).Session;

            var blobs = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out _);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await builder.SynchronizeAsync(stagingSession.StagingRoot));

            ex.ShouldNotBeNull();
            ex.Message.ShouldContain("ValidateAsync");
            blobs.Uploaded.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_StartsMultipleFileTreeUploadsBeforeReturning()
    {
        const string accountName = "acc-parallel";
        const string containerName = "con-parallel";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = (await CreateStagingAsync(
                accountName,
                containerName,
                ("photos/2024/june/a.jpg", FakeContentHash('7'), now, now),
                ("photos/2024/june/b.jpg", FakeContentHash('8'), now, now),
                ("docs/report.pdf", FakeContentHash('9'), now, now))).Session;

            var blobs = new BlockingFileTreeUploadBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            var synchronizeTask = builder.SynchronizeAsync(stagingSession.StagingRoot);
            var sawTwoConcurrentStarts = await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(1));

            blobs.AllowUploads();

            var root = await synchronizeTask.WaitAsync(TimeSpan.FromSeconds(5));
            root.ShouldNotBeNull();
            sawTwoConcurrentStarts.ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_CancelsProducerWhenUploadWorkerFaults()
    {
        const string accountName = "acc-upload-failure";
        const string containerName = "con-upload-failure";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            const string hexChars = "0123456789abcdef";
            var files = Enumerable.Range(0, 32)
                .Select(i => (
                    Path: $"dir-{i:D2}/file.txt",
                    Hash: FakeContentHash(hexChars[i % hexChars.Length]),
                    Created: now,
                    Modified: now))
                .ToArray();

            await using var stagingSession = (await CreateStagingAsync(accountName, containerName, files)).Session;

            var blobs = new FaultingAndBlockingFileTreeUploadBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await builder.SynchronizeAsync(stagingSession.StagingRoot).WaitAsync(TimeSpan.FromSeconds(5)));

            ex.ShouldNotBeNull();
            ex.Message.ShouldContain("Simulated filetree upload failure.");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task BlockingUploadBlobContainerService_RecordsAllConcurrentUploads()
    {
        var blobs = new BlockingFileTreeUploadBlobContainerService();
        var uploads = Enumerable.Range(0, 2_000)
            .Select(async i =>
            {
                using var content = new MemoryStream();
                await blobs.UploadAsync($"{BlobPaths.FileTrees}/blob-{i}", content, new Dictionary<string, string>(), BlobTier.Hot);
            })
            .ToArray();

        var sawTwoConcurrentStarts = await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(1));
        sawTwoConcurrentStarts.ShouldBeTrue();

        blobs.AllowUploads();
        await Task.WhenAll(uploads);

        blobs.Uploaded.Count.ShouldBe(2_000);
        blobs.Uploaded.Keys.ShouldContain($"{BlobPaths.FileTrees}/blob-0");
        blobs.Uploaded.Keys.ShouldContain($"{BlobPaths.FileTrees}/blob-1999");
    }

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc = new PlaintextPassthroughService();
        IReadOnlyList<FileTreeEntry> entries =
        [
            new FileEntry
            {
                Name = SegmentOf("file.txt"),
                ContentHash = FakeContentHash('d'),
                Created = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero)
            }
        ];

        var h1 = FileTreeBuilder.ComputeHash(entries, enc);
        var h2 = FileTreeBuilder.ComputeHash(entries, enc);

        h1.ShouldBe(h2);
    }

    [Test]
    public void ComputeHash_MetadataChange_ProducesNewHash()
    {
        var enc = new PlaintextPassthroughService();
        IReadOnlyList<FileTreeEntry> entries1 =
        [
            new FileEntry
            {
                Name = SegmentOf("file.txt"),
                ContentHash = FakeContentHash('d'),
                Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }
        ];
        IReadOnlyList<FileTreeEntry> entries2 =
        [
            ((FileEntry)entries1[0]) with
            {
                Modified = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero)
            }
        ];

        var h1 = FileTreeBuilder.ComputeHash(entries1, enc);
        var h2 = FileTreeBuilder.ComputeHash(entries2, enc);

        h1.ShouldNotBe(h2);
    }

    [Test]
    public void ComputeHash_WithPassphrase_DifferentFromPlaintext()
    {
        var entries = (IReadOnlyList<FileTreeEntry>)
        [
            new FileEntry
            {
                Name = SegmentOf("file.txt"),
                ContentHash = FakeContentHash('a'),
                Created = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero)
            }
        ];
        var plain = new PlaintextPassthroughService();
        var withPass = new PassphraseEncryptionService("secret");

        var h1 = FileTreeBuilder.ComputeHash(entries, plain);
        var h2 = FileTreeBuilder.ComputeHash(entries, withPass);

        h1.ShouldNotBe(h2);
    }

    [Test]
    public void ParseStagedNodeEntryLine_RejectsNonHashDirectoryIds()
    {
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine("not-a-directory-id D child"));
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine($"{new string('a', 63)} D child"));
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine($"{new string('g', 64)} D child"));
    }

    [Test]
    [Arguments("child/grandchild/")]
    [Arguments("child\\")]
    [Arguments("./")]
    [Arguments("../")]
    public void ParseStagedNodeEntryLine_RejectsNonCanonicalNames(string name)
    {
        var directoryId = new string('a', 64);

        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine($"{directoryId} D {name}"));
    }
        
    [Test]
    public async Task SynchronizeAsync_NestedDirectories_ProducesStableRootHash()
    {
        const string accountName = "acc-nested-core";
        const string containerName = "con-nested-core";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var blobs = new FakeRecordingBlobContainerService();
            FileTreeHash? root1;
            await using (var stagingSession1 = (await CreateStagingAsync(
                accountName,
                containerName,
                ("a/b/c/file.txt", FakeContentHash('a'), now, now),
                ("a/b/other.txt", FakeContentHash('b'), now, now),
                ("z.txt", FakeContentHash('c'), now, now))).Session)
            {
                var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
                await fileTreeService.ValidateAsync();
                root1 = await builder.SynchronizeAsync(stagingSession1.StagingRoot);
            }

            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            var blobs2 = new FakeRecordingBlobContainerService();
            FileTreeHash? root2;
            await using (var stagingSession2 = (await CreateStagingAsync(
                accountName,
                containerName,
                ("a/b/c/file.txt", FakeContentHash('a'), now, now),
                ("a/b/other.txt", FakeContentHash('b'), now, now),
                ("z.txt", FakeContentHash('c'), now, now))).Session)
            {
                var builder2 = CreateBuilder(blobs2, accountName, containerName, out var fileTreeService2);
                await fileTreeService2.ValidateAsync();
                root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
            }

            root1.ShouldNotBeNull();
            root2.ShouldBe(root1);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_SameEntriesDifferentStagingOrder_ProducesSameRootHash()
    {
        const string accountName = "acc-ordering";
        const string containerName = "con-ordering";
        var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var blobs1 = new FakeRecordingBlobContainerService();
            FileTreeHash? root1;
            await using (var stagingSession1 = (await CreateStagingAsync(
                accountName,
                containerName,
                ("b.txt", FakeContentHash('1'), now, now),
                ("a.txt", FakeContentHash('2'), now, now),
                ("docs/z.txt", FakeContentHash('3'), now, now),
                ("docs/a.txt", FakeContentHash('4'), now, now))).Session)
            {
                var builder1 = CreateBuilder(blobs1, accountName, containerName, out var fileTreeService1);
                await fileTreeService1.ValidateAsync();
                root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);
            }

            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            var blobs2 = new FakeRecordingBlobContainerService();
            FileTreeHash? root2;
            await using (var stagingSession2 = (await CreateStagingAsync(
                accountName,
                containerName,
                ("docs/a.txt", FakeContentHash('4'), now, now),
                ("docs/z.txt", FakeContentHash('3'), now, now),
                ("a.txt", FakeContentHash('2'), now, now),
                ("b.txt", FakeContentHash('1'), now, now))).Session)
            {
                var builder2 = CreateBuilder(blobs2, accountName, containerName, out var fileTreeService2);
                await fileTreeService2.ValidateAsync();
                root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
            }

            root1.ShouldNotBeNull();
            root2.ShouldBe(root1);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

}
