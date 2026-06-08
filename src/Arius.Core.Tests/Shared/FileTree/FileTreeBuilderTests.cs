using System.Runtime.CompilerServices;
using Arius.Core.Shared.FileTree;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.FileTree.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBuilderTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    private static async Task<(FileTreeStagingSession Session, LocalDirectory StagingRoot)> CreateStagingAsync(RepositoryTestFixture fixture, params (string Path, ContentHash Hash, DateTimeOffset Created, DateTimeOffset Modified)[] files)
    {
        var       session = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        using var writer  = new FileTreeStagingWriter(session.StagingRoot);

        foreach (var file in files)
            await writer.AppendFileEntryAsync(RelativePath.Parse(file.Path), file.Hash, file.Created, file.Modified);

        return (session, session.StagingRoot);
    }

    private static async Task WriteNodeLinesAsync(LocalDirectory stagingRoot, PathSegment directoryId, params string[] lines)
    {
        await File.WriteAllLinesAsync(stagingRoot.Resolve(FileTreePaths.GetStagingNodePath(directoryId)), lines);
    }

    [Test]
    public async Task SynchronizeAsync_EmptyManifest_ReturnsNull()
    {
        const string accountName = "account-empty";
        const string cont        = "container-empty";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, cont, s_enc);

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();
        await using var stagingSession = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        var root = await builder.SynchronizeAsync(stagingSession.StagingRoot);

        root.ShouldBeNull();
        ((FakeRecordingBlobContainerService)fixture.BlobContainer).Uploaded.ShouldBeEmpty();
    }

    [Test]
    public async Task SynchronizeAsync_SingleFile_RootTreeUploaded()
    {
        var accountName = $"unittest-acct-single-{Guid.NewGuid():N}";
        var containerName = $"cont-single-{Guid.NewGuid():N}";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var (stagingSession, stagingRoot) = await CreateStagingAsync(fixture, ("readme.txt", FakeContentHash('b'), now, now));

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();
        await using (stagingSession)
        {
            var root = await builder.SynchronizeAsync(stagingRoot);

            root.ShouldNotBeNull();
            ((FakeRecordingBlobContainerService)fixture.BlobContainer).Uploaded.Count.ShouldBeGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task SynchronizeAsync_SingleFile_LogsBuildAndSynchronizeSummary()
    {
        var accountName = $"unittest-acct-logs-{Guid.NewGuid():N}";
        var containerName = $"cont-logs-{Guid.NewGuid():N}";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var (stagingSession, stagingRoot) = await CreateStagingAsync(fixture, ("readme.txt", FakeContentHash('b'), now, now));

        var logger  = new FakeLogger<FileTreeBuilder>();
        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService, logger);
        await fixture.FileTreeService.ValidateAsync();
        await using (stagingSession)
        {
            await builder.SynchronizeAsync(stagingRoot);
        }

        var messages = logger.Collector.GetSnapshot().Select(record => record.Message).ToArray();
        messages.ShouldContain(message => message.Contains("Building filetree from staging root", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("Synchronized", StringComparison.Ordinal));
    }

    [Test]
    public async Task SynchronizeAsync_RelativePathStagedFile_ProducesRootHash()
    {
        var accountName = $"unittest-acct-single-relative-{Guid.NewGuid():N}";
        var containerName = $"cont-single-relative-{Guid.NewGuid():N}";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        await using var stagingSession = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        using (var writer = new FileTreeStagingWriter(stagingSession.StagingRoot))
        {
            await writer.AppendFileEntryAsync(RelativePath.Parse("docs/readme.txt"), FakeContentHash('b'), now, now);
        }

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();

        var root = await builder.SynchronizeAsync(stagingSession.StagingRoot);

        root.ShouldNotBeNull();
        ((FakeRecordingBlobContainerService)fixture.BlobContainer).Uploaded.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task SynchronizeAsync_IdenticalManifest_SameRootHash()
    {
        const string acct1 = "acc-identical-1";
        const string cont1 = "con-identical-1";
        const string acct2 = "acc-identical-2";
        const string cont2 = "con-identical-2";
        await using var fixture1 = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), acct1, cont1, s_enc);
        await using var fixture2 = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), acct2, cont2, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        await using var stagingSession1 = (await CreateStagingAsync(
            fixture1,
            ("photos/a.jpg", FakeContentHash('c'), now, now),
            ("photos/b.jpg", FakeContentHash('d'), now, now),
            ("docs/r.pdf", FakeContentHash('e'), now, now))).Session;
        await using var stagingSession2 = (await CreateStagingAsync(
            fixture2,
            ("photos/a.jpg", FakeContentHash('c'), now, now),
            ("photos/b.jpg", FakeContentHash('d'), now, now),
            ("docs/r.pdf", FakeContentHash('e'), now, now))).Session;

        var builder1 = new FileTreeBuilder(s_enc, fixture1.FileTreeService);
        var builder2 = new FileTreeBuilder(s_enc, fixture2.FileTreeService);
        await fixture1.FileTreeService.ValidateAsync();
        await fixture2.FileTreeService.ValidateAsync();
        var root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);
        var root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);

        root1.ShouldBe(root2);
    }

    [Test]
    public async Task SynchronizeAsync_MetadataChange_DifferentRootHash()
    {
        const string accountName   = "acc-meta";
        const string containerName = "con-meta";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now1  = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var now2  = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        FileTreeHash? root1;
        await using (var stagingSession1 = (await CreateStagingAsync(fixture, [ ("file.txt", FakeContentHash('f'), now1, now1) ])).Session)
        {
            var builder1 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);
        }

        fixture.DeleteLocalCacheDirectory(recreate: true);

        FileTreeHash? root2;
        await using (var stagingSession2 = (await CreateStagingAsync(fixture, [ ("file.txt", FakeContentHash('f'), now1, now2) ])).Session)
        {
            var builder2 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
        }

        root1.ShouldNotBe(root2);
    }

    [Test]
    public async Task SynchronizeAsync_DuplicateFileNamesInOneDirectory_Throws()
    {
        const string accountName   = "acc-dup-file";
        const string containerName = "con-dup-file";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
        var first = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = PathSegment.Parse("a.txt"),
            ContentHash = FakeContentHash('a'),
            Created = now,
            Modified = now
        });
        var second = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = PathSegment.Parse("a.txt"),
            ContentHash = FakeContentHash('b'),
            Created = now,
            Modified = now
        });

        await using var stagingSession = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        await WriteNodeLinesAsync(stagingSession.StagingRoot, rootId, first, second);

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => builder.SynchronizeAsync(stagingSession.StagingRoot));
    }

    [Test]
    public async Task ReadNodeEntriesAsync_DuplicateFileDetectedBeforeSourceIsFullyEnumerated()
    {
        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var enumerationAdvancedPastDuplicate = false;
        var first = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = PathSegment.Parse("a.txt"),
            ContentHash = FakeContentHash('a'),
            Created = now,
            Modified = now
        });
        var duplicate = FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
        {
            Name = PathSegment.Parse("a.txt"),
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
        const string accountName   = "acc-dup-dir";
        const string containerName = "con-dup-dir";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var childId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));
        var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);

        await using var stagingSession1 = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        await WriteNodeLinesAsync(stagingSession1.StagingRoot, rootId, [ $"{childId} D photos/", $"{childId} D photos/" ]);
        await WriteNodeLinesAsync(
            stagingSession1.StagingRoot,
            childId,
            FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
            {
                Name = PathSegment.Parse("a.jpg"),
                ContentHash = FakeContentHash('c'),
                Created = now,
                Modified = now
            }));

        var builder1 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();
        var root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);

        await stagingSession1.DisposeAsync();
        fixture.DeleteLocalCacheDirectory(recreate: true);

        await using var stagingSession2 = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);
        await WriteNodeLinesAsync(stagingSession2.StagingRoot, rootId, [ $"{childId} D photos/" ]);
        await WriteNodeLinesAsync(
            stagingSession2.StagingRoot,
            childId,
            FileTreeSerializer.SerializePersistedFileEntryLine(new FileEntry
            {
                Name = PathSegment.Parse("a.jpg"),
                ContentHash = FakeContentHash('c'),
                Created = now,
                Modified = now
            }));

        var builder2 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();
        var root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);

        root1.ShouldBe(root2);
    }

    [Test]
    public async Task SynchronizeAsync_CalculatesSiblingNodes_WhileUploadsAreBlocked()
    {
        const string accountName = "unittest-acc-blocked-uploads";
        const string containerName = "con-blocked-uploads";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new BlockingFileTreeUploadBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        await using var stagingSession = (await CreateStagingAsync(
            fixture,
            ("photos/a.jpg", FakeContentHash('d'), now, now),
            ("docs/b.jpg", FakeContentHash('e'), now, now))).Session;

        var blobs = (BlockingFileTreeUploadBlobContainerService)fixture.BlobContainer;
        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();

        var syncTask = builder.SynchronizeAsync(stagingSession.StagingRoot);

        (await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        blobs.AllowUploads();

        (await syncTask).ShouldNotBeNull();
    }

    [Test]
    public async Task SynchronizeAsync_DeduplicatesBlob_WhenAlreadyOnDisk()
    {
        const string accountName = "unittest-acc";
        const string containerName = "con";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        FileTreeHash? root;
        await using (var stagingSession1 = (await CreateStagingAsync(fixture, ("file.txt", FakeContentHash('1'), now, now))).Session)
        {
            var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root = await builder.SynchronizeAsync(stagingSession1.StagingRoot);
        }

        var firstBlobs = (FakeRecordingBlobContainerService)fixture.BlobContainer;
        firstBlobs.Uploaded.Count.ShouldBeGreaterThan(0);

        var blobs2 = new FakeRecordingBlobContainerService();
        foreach (var blobName in firstBlobs.Uploaded)
            blobs2.SeedRemoteBlob(blobName);

        await using var fixture2 = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs2, accountName, containerName, s_enc);
        FileTreeHash? root2;
        await using (var stagingSession2 = (await CreateStagingAsync(fixture2, ("file.txt", FakeContentHash('1'), now, now))).Session)
        {
            var builder2 = new FileTreeBuilder(s_enc, fixture2.FileTreeService);
            await fixture2.FileTreeService.ValidateAsync();
            root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
        }

        root2.ShouldBe(root);
        blobs2.Uploaded.Count.ShouldBe(0);
    }

    [Test]
    public async Task SynchronizeAsync_WithoutValidation_FailsFastBeforeUpload()
    {
        const string accountName   = "acc-unvalidated";
        const string containerName = "con-unvalidated";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var stagingSession = (await CreateStagingAsync(fixture, ("file.txt", FakeContentHash('2'), now, now))).Session;

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.SynchronizeAsync(stagingSession.StagingRoot));

        ex.ShouldNotBeNull();
        ex.Message.ShouldContain("ValidateAsync");
        ((FakeRecordingBlobContainerService)fixture.BlobContainer).Uploaded.ShouldBeEmpty();
    }

    [Test]
    [NotInParallel("FileTreeBuilderParallelUploadTests")]
    public async Task SynchronizeAsync_StartsMultipleFileTreeUploadsBeforeReturning()
    {
        var accountName = $"unittest-acc-parallel-{Guid.NewGuid():N}";
        var containerName = $"con-parallel-{Guid.NewGuid():N}";
        ThreadPool.GetMinThreads(out var originalWorkerThreads, out var originalCompletionPortThreads);
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new BlockingFileTreeUploadBlobContainerService(), accountName, containerName, s_enc);

        try
        {
            ThreadPool.SetMinThreads(Math.Max(originalWorkerThreads, 32), originalCompletionPortThreads);

            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = (await CreateStagingAsync(
                fixture,
                ("photos/2024/june/a.jpg", FakeContentHash('7'), now, now),
                ("photos/2024/june/b.jpg", FakeContentHash('8'), now, now),
                ("docs/report.pdf", FakeContentHash('9'), now, now))).Session;

            var blobs = (BlockingFileTreeUploadBlobContainerService)fixture.BlobContainer;
            var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();

            var synchronizeTask = builder.SynchronizeAsync(stagingSession.StagingRoot);
            var sawTwoConcurrentStarts = await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(5));

            blobs.AllowUploads();

            var root = await synchronizeTask.WaitAsync(TimeSpan.FromSeconds(5));
            root.ShouldNotBeNull();
            sawTwoConcurrentStarts.ShouldBeTrue();
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkerThreads, originalCompletionPortThreads);
        }
    }

    [Test]
    public async Task SynchronizeAsync_CancelsProducerWhenUploadWorkerFaults()
    {
        const string accountName   = "acc-upload-failure";
        const string containerName = "con-upload-failure";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FaultingAndBlockingFileTreeUploadBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        const string hexChars = "0123456789abcdef";
        var files = Enumerable.Range(0, 32)
            .Select(i => (
                Path: $"dir-{i:D2}/file.txt",
                Hash: FakeContentHash(hexChars[i % hexChars.Length]),
                Created: now,
                Modified: now))
            .ToArray();

        await using var stagingSession = (await CreateStagingAsync(fixture, files)).Session;

        var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
        await fixture.FileTreeService.ValidateAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.SynchronizeAsync(stagingSession.StagingRoot).WaitAsync(TimeSpan.FromSeconds(5)));

        ex.ShouldNotBeNull();
        ex.Message.ShouldContain("Simulated filetree upload failure.");
    }

    [Test]
    public async Task BlockingUploadBlobContainerService_RecordsAllConcurrentUploads()
    {
        var blobs = new BlockingFileTreeUploadBlobContainerService();
        var uploads = Enumerable.Range(0, 2_000)
            .Select(async i =>
            {
                using var content = new MemoryStream();
                await blobs.UploadAsync(BlobPaths.FileTreePath($"blob-{i}"), content, new Dictionary<string, string>(), BlobTier.Hot);
            })
            .ToArray();

        var sawTwoConcurrentStarts = await blobs.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(1));
        sawTwoConcurrentStarts.ShouldBeTrue();

        blobs.AllowUploads();
        await Task.WhenAll(uploads);

        blobs.Uploaded.Count.ShouldBe(2_000);
        blobs.Uploaded.Keys.ShouldContain(BlobPaths.FileTreePath("blob-0"));
        blobs.Uploaded.Keys.ShouldContain(BlobPaths.FileTreePath("blob-1999"));
    }

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc = new PlaintextPassthroughService();
        IReadOnlyList<FileTreeEntry> entries =
        [
            new FileEntry
            {
                Name = PathSegment.Parse("file.txt"),
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
                Name = PathSegment.Parse("file.txt"),
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
                Name = PathSegment.Parse("file.txt"),
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
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine("not-a-directory-id D child/"));
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine($"{new string('a', 63)} D child/"));
        Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine($"{new string('g', 64)} D child/"));
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
        const string accountName   = "acc-nested-core";
        const string containerName = "con-nested-core";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        FileTreeHash? root1;
        await using (var stagingSession1 = (await CreateStagingAsync(
            fixture,
            ("a/b/c/file.txt", FakeContentHash('a'), now, now),
            ("a/b/other.txt", FakeContentHash('b'), now, now),
            ("z.txt", FakeContentHash('c'), now, now))).Session)
        {
            var builder = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root1 = await builder.SynchronizeAsync(stagingSession1.StagingRoot);
        }

        fixture.DeleteLocalCacheDirectory(recreate: true);

        FileTreeHash? root2;
        await using (var stagingSession2 = (await CreateStagingAsync(
            fixture,
            ("a/b/c/file.txt", FakeContentHash('a'), now, now),
            ("a/b/other.txt", FakeContentHash('b'), now, now),
            ("z.txt", FakeContentHash('c'), now, now))).Session)
        {
            var builder2 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
        }

        root1.ShouldNotBeNull();
        root2.ShouldBe(root1);
    }

    [Test]
    public async Task SynchronizeAsync_SameEntriesDifferentStagingOrder_ProducesSameRootHash()
    {
        const string accountName   = "acc-ordering";
        const string containerName = "con-ordering";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(new FakeRecordingBlobContainerService(), accountName, containerName, s_enc);

        var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        FileTreeHash? root1;
        await using (var stagingSession1 = (await CreateStagingAsync(
            fixture,
            ("b.txt", FakeContentHash('1'), now, now),
            ("a.txt", FakeContentHash('2'), now, now),
            ("docs/z.txt", FakeContentHash('3'), now, now),
            ("docs/a.txt", FakeContentHash('4'), now, now))).Session)
        {
            var builder1 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root1 = await builder1.SynchronizeAsync(stagingSession1.StagingRoot);
        }

        fixture.DeleteLocalCacheDirectory(recreate: true);

        FileTreeHash? root2;
        await using (var stagingSession2 = (await CreateStagingAsync(
            fixture,
            ("docs/a.txt", FakeContentHash('4'), now, now),
            ("docs/z.txt", FakeContentHash('3'), now, now),
            ("a.txt", FakeContentHash('2'), now, now),
            ("b.txt", FakeContentHash('1'), now, now))).Session)
        {
            var builder2 = new FileTreeBuilder(s_enc, fixture.FileTreeService);
            await fixture.FileTreeService.ValidateAsync();
            root2 = await builder2.SynchronizeAsync(stagingSession2.StagingRoot);
        }

        root1.ShouldNotBeNull();
        root2.ShouldBe(root1);
    }
}
