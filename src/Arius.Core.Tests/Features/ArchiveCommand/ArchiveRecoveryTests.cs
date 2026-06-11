using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.FileTree;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using System.Collections.Concurrent;
using System.Formats.Tar;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class ArchiveRecoveryTests
{
    [Test]
    [MatrixDataSource]
    public async Task Archive_LargeBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("large.bin"), 2 * 1024 * 1024);
        var contentHash = fixture.Encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);

        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkPath(chunkHash), content, uploadTier);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.ChunkPath(chunkHash));

        var result = await ArchiveAsync(fixture, uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        using var resumedIndex = new ChunkIndexService(blobs, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
    }

    [Test]
    [MatrixDataSource]
    public async Task Archive_TarBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("small.txt"), 256);
        var contentHash = fixture.Encryption.ComputeHash(content);

        var tarHash = ComputeTarHash(fixture, contentHash, content);
        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        await blobs.SeedTarBlobAsync(BlobPaths.ChunkPath(tarHash), [content], uploadTier);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.ChunkPath(tarHash));

        var result = await ArchiveAsync(fixture, uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        using var resumedIndex = new ChunkIndexService(blobs, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_LargeBlobWithoutMetadata_Rerun_DeletesAndRetries()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("partial.bin"), 2 * 1024 * 1024);
        var contentHash = fixture.Encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);
        var blobName = BlobPaths.ChunkPath(chunkHash);

        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await ArchiveAsync(fixture, BlobTier.Archive);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var finalMeta = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash));
        finalMeta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    [Test]
    public async Task Archive_NewContent_CreatesSnapshotWithRootHash()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 1024);

        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.RootHash.ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_AfterSnapshotCreation_FreshInstanceLookupUsesPromotedCacheWithoutRevalidation()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        var content = await WriteRandomFileAsync(fixture, relativePath, 2 * 1024 * 1024);
        var contentHash = fixture.Encryption.ComputeHash(content);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;

        var result = await ArchiveAsync(fixture, BlobTier.Cool, smallFileThreshold: 0);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        using var resumedIndex = new ChunkIndexService(blobs, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        blobs.RequestedBlobNames.Clear();

        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
        blobs.RequestedBlobNames.ShouldNotContain(shardBlobName);
    }

    [Test]
    public async Task Archive_WhenChunkIndexFlushFails_DoesNotPublishSnapshotAndKeepsSessionEntries()
    {
        var blobs = new FaultingChunkIndexUploadBlobContainerService(new FakeInMemoryBlobContainerService());
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(
            blobs,
            "test-account",
            $"test-container-{Guid.NewGuid():N}",
            new PlaintextPassthroughService(),
            TestTempRoots.CreateDirectory("archive-test"));
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 2 * 1024 * 1024);
        var contentHash = fixture.Encryption.ComputeHash(content);

        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeFalse();
        (await fixture.Snapshot.ResolveAsync()).ShouldBeNull();

        blobs.FailChunkIndexUploads = false;
        using var resumedIndex = new ChunkIndexService(blobs, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
        await resumedIndex.FlushAsync();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => resumedIndex.LookupAsync(contentHash));
        ex.Message.ShouldBe("Chunk-index service cannot be used after flush has started.");
    }

    [Test]
    public async Task Archive_RecordsLargeDirtyRowOnlyAfterUploadLargeReturns()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 2 * 1024 * 1024);
        var contentHash = fixture.Encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);
        var observedMissingDuringUpload = false;

        var chunkStorage = new RecordingChunkStorageService(
            uploadLargeAsync: async (actualChunkHash, _, sourceSize, _, _, _) =>
            {
                actualChunkHash.ShouldBe(chunkHash);
                (await fixture.Index.LookupAsync(contentHash)).ShouldBeNull();
                observedMissingDuringUpload = true;
                return new ChunkUploadResult(actualChunkHash, StoredSize: sourceSize / 2, AlreadyExisted: false, OriginalSize: sourceSize);
            });

        var handler = new ArchiveCommandHandler(
            fixture.BlobContainer,
            fixture.Encryption,
            fixture.Index,
            chunkStorage,
            fixture.FileTreeService,
            fixture.Snapshot,
            fixture.Mediator,
            new FakeLogger<ArchiveCommandHandler>(),
            NullLoggerFactory.Instance,
            fixture.AccountName,
            fixture.ContainerName);

        var result = await handler.Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
                SmallFileThreshold = 0,
            }),
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        observedMissingDuringUpload.ShouldBeTrue();
        using var resumedIndex = new ChunkIndexService((FakeInMemoryBlobContainerService)fixture.BlobContainer, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_RecordsThinDirtyRowOnlyAfterUploadThinReturns()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var content = await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 256);
        var contentHash = fixture.Encryption.ComputeHash(content);
        var tarUploaded = false;
        var observedMissingDuringThinUpload = false;

        var chunkStorage = new RecordingChunkStorageService(
            uploadTarAsync: (tarHash, _, sourceSize, _, _, _) =>
            {
                tarUploaded = true;
                return Task.FromResult(new ChunkUploadResult(tarHash, StoredSize: sourceSize / 2, AlreadyExisted: false, OriginalSize: sourceSize));
            },
            uploadThinAsync: async (actualContentHash, _, _, _, _) =>
            {
                tarUploaded.ShouldBeTrue();
                actualContentHash.ShouldBe(contentHash);
                (await fixture.Index.LookupAsync(contentHash)).ShouldBeNull();
                observedMissingDuringThinUpload = true;
                return true;
            });

        var handler = new ArchiveCommandHandler(
            fixture.BlobContainer,
            fixture.Encryption,
            fixture.Index,
            chunkStorage,
            fixture.FileTreeService,
            fixture.Snapshot,
            fixture.Mediator,
            new FakeLogger<ArchiveCommandHandler>(),
            NullLoggerFactory.Instance,
            fixture.AccountName,
            fixture.ContainerName);

        var result = await handler.Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024,
            }),
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        observedMissingDuringThinUpload.ShouldBeTrue();
        using var resumedIndex = new ChunkIndexService((FakeInMemoryBlobContainerService)fixture.BlobContainer, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(contentHash)).ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_NewContent_EmitsConsistentPhaseTimingLogs()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 1024);

        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var messages = fixture.ArchiveLogs
            .GetSnapshot(clear: false)
            .Select(static record => record.Message)
            .ToArray();

        var phases = messages
            .Where(static message => message.StartsWith("[phase] ", StringComparison.Ordinal))
            .Select(static message => message[8..])
            .ToArray();

        phases.ShouldBe([
            "ensure-container",
            "open-staging",
            "enumerate",
            "hash",
            "dedup-route",
            "large-upload",
            "tar-build",
            "tar-upload",
            "chunk-index-update",
            "filetree-update",
            "await-workers",
            "validate-filetrees",
            "flush-chunkindex-and-synchronize-filetree",
            "snapshot",
            "write-pointers",
            "delete-local",
            "complete"
        ]);
    }

    [Test]
    public async Task Archive_WritesFileTreeEntryWithBinaryFileTimestamps()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        var created = new DateTimeOffset(2021, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var modified = new DateTimeOffset(2022, 5, 6, 7, 8, 9, TimeSpan.Zero);

        await WriteRandomFileAsync(fixture, relativePath, 128);
        fixture.LocalFileSystem.SetTimestamps(relativePath, created, modified);

        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var entry = await ReadRootFileEntryAsync(fixture, relativePath);

        if (!OperatingSystem.IsLinux())
            entry.Created.ShouldBe(created);

        entry.Modified.ShouldBe(modified);
    }

    [Test]
    public async Task Archive_UsesEnumeratedBinaryMetadataForScanAndHashProgress()
    {
        await using var fixture = await CreateArchiveFixtureAsync();

        var mediator = fixture.Mediator;
        var scannedEvents = new ConcurrentBag<FileScannedEvent>();
        var hashingEvents = new ConcurrentBag<FileHashingEvent>();
        mediator
            .When(x => x.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                if (callInfo.ArgAt<INotification>(0) is FileScannedEvent scanned)
                    scannedEvents.Add(scanned);
                else if (callInfo.ArgAt<INotification>(0) is FileHashingEvent hashing) 
                    hashingEvents.Add(hashing);
            });

        await WriteRandomFileAsync(fixture, RelativePath.Parse("photos/pic.jpg"), 32);
        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        var scannedEvent = scannedEvents.ShouldHaveSingleItem();
        scannedEvent.RelativePath.ShouldBe(RelativePath.Parse("photos/pic.jpg"));
        scannedEvent.FileSize.ShouldBe(32);

        var hashingEvent = hashingEvents.ShouldHaveSingleItem();
        hashingEvent.RelativePath.ShouldBe(RelativePath.Parse("photos/pic.jpg"));
        hashingEvent.FileSize.ShouldBe(32);
    }

    [Test]
    public async Task Archive_RemoveLocal_WritesPointerAndDeletesBinaryAtRelativePath()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        await WriteRandomFileAsync(fixture, relativePath, 128);

        var result = await ArchiveAsync(fixture, BlobTier.Cool, removeLocal: true);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.LocalFileSystem.FileExists(relativePath).ShouldBeFalse();
        fixture.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue();
    }

    [Test]
    public async Task Archive_RemoveLocal_DeletesDeduplicatedBinaryToo()
    {
        // Two files with identical content: one is uploaded, the other deduplicates against it within
        // the same run. Both binaries are durably archived, so --remove-local must delete both (the
        // delete decision is "has a local binary that is archived", not "was uploaded this run").
        await using var fixture = await CreateArchiveFixtureAsync();
        var uploaded   = RelativePath.Parse("a/original.bin");
        var deduped    = RelativePath.Parse("b/duplicate.bin");
        var content    = new byte[256];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(uploaded, content, CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(deduped, content, CancellationToken.None);

        var result = await ArchiveAsync(fixture, BlobTier.Cool, removeLocal: true);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.LocalFileSystem.FileExists(uploaded).ShouldBeFalse();
        fixture.LocalFileSystem.FileExists(deduped).ShouldBeFalse();
        fixture.LocalFileSystem.FileExists(uploaded.ToPointerPath()).ShouldBeTrue();
        fixture.LocalFileSystem.FileExists(deduped.ToPointerPath()).ShouldBeTrue();
    }

    [Test]
    public async Task Archive_RemoveLocalAndNoPointers_ReturnsValidationFailure()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 128);

        var result = await ArchiveAsync(fixture, BlobTier.Cool, removeLocal: true, noPointers: true);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("--remove-local cannot be combined with --no-pointers");
    }

    [Test]
    public async Task Archive_WhenAnotherLocalRunHoldsStagingLock_FailsFast()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 1024);

        await using var stagingSession = await FileTreeStagingSession.OpenAsync(fixture.FileTreeCacheDirectory);

        var result = await ArchiveAsync(fixture, BlobTier.Cool);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("staging", Case.Insensitive);
        result.ErrorMessage.ShouldContain("already open", Case.Insensitive);
    }

    [Test]
    public async Task Archive_WhenCancelledBeforeOpeningStagingSession_PropagatesCancellation()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 1024);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ArchiveAsync(fixture, BlobTier.Cool, cancellationToken: cts.Token));
    }

    [Test]
    public async Task Archive_WhenOpeningStagingSessionThrowsNonIoException_ReturnsFailedResult()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("docs/readme.txt"), 1024);

        var result = await ArchiveAsync(
            fixture,
            BlobTier.Cool,
            openStagingSession: (_, _) => throw new InvalidOperationException("staging setup failed"));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("staging setup failed");
    }

    // ── Hang regression: a single unreadable file must never deadlock the pipeline ──

    [Test]
    public async Task Archive_TreeWithBrokenSymlinkAndManyFiles_CompletesAndSkipsLink()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        await using var fixture = await CreateArchiveFixtureAsync();

        const int fileCount = 70; // > ChannelCapacity (64): fills the bounded enumerate→hash channel
        for (var i = 0; i < fileCount; i++)
            await WriteRandomFileAsync(fixture, RelativePath.Parse($"files/file-{i:D3}.bin"), 256);

        // Dangling symlink: enumerated by the OS, FileInfo.Length reports the link text, OpenRead throws.
        var brokenLink = Path.Combine(fixture.LocalDirectory.ToString(), "files", "broken.bin");
        File.CreateSymbolicLink(brokenLink, "/nonexistent/target");

        var result = await ArchiveAsync(fixture, BlobTier.Cool).WaitAsync(TimeSpan.FromSeconds(120));

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.FilesScanned.ShouldBe(fileCount); // broken link filtered out during enumeration
        result.RootHash.ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_WhenHashStageThrowsForOneFile_CompletesWithoutHanging()
    {
        await using var fixture = await CreateArchiveFixtureAsync();

        const int fileCount = 70; // > ChannelCapacity (64): a faulting hash worker would block the producer
        for (var i = 0; i < fileCount; i++)
            await WriteRandomFileAsync(fixture, RelativePath.Parse($"f/file-{i:D3}.bin"), 256);

        var poison = RelativePath.Parse("f/file-000.bin");
        fixture.Mediator
            .When(x => x.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                if (callInfo.ArgAt<INotification>(0) is FileHashingEvent e && e.RelativePath.Equals(poison))
                    throw new IOException("simulated unreadable file");
            });

        var result = await ArchiveAsync(fixture, BlobTier.Cool).WaitAsync(TimeSpan.FromSeconds(120));

        result.Success.ShouldBeTrue(result.ErrorMessage);
        fixture.ArchiveLogs.GetSnapshot()
            .ShouldContain(record => record.Level == LogLevel.Warning &&
                                     record.Message.Contains("Skipping unreadable file during hashing") &&
                                     record.Message.Contains(poison.ToString()));
    }

    [Test]
    [MatrixDataSource]
    public async Task Archive_RecordsUploadTierAsStorageTierHint_ForLargeAndTarBackedFiles(
        [Matrix(BlobTier.Cool, BlobTier.Archive)] BlobTier uploadTier)
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        var largeContent = await WriteRandomFileAsync(fixture, RelativePath.Parse("large.bin"), 2 * 1024 * 1024);
        var smallContent = await WriteRandomFileAsync(fixture, RelativePath.Parse("small.txt"), 256);

        var result = await ArchiveAsync(fixture, uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        using var resumedIndex = new ChunkIndexService(blobs, fixture.Encryption, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        (await resumedIndex.LookupAsync(fixture.Encryption.ComputeHash(largeContent)))!.StorageTierHint.ShouldBe(uploadTier);
        (await resumedIndex.LookupAsync(fixture.Encryption.ComputeHash(smallContent)))!.StorageTierHint.ShouldBe(uploadTier);
    }

    private static async ValueTask<RepositoryTestFixture> CreateArchiveFixtureAsync()
        => await RepositoryTestFixture.CreateWithEncryptionAsync(
            new FakeInMemoryBlobContainerService(),
            "test-account",
            $"test-container-{Guid.NewGuid():N}",
            new PlaintextPassthroughService(),
            TestTempRoots.CreateDirectory("archive-test"));

    private static async Task<byte[]> WriteRandomFileAsync(RepositoryTestFixture fixture, RelativePath relativePath, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);
        return content;
    }

    private static async Task<FileEntry> ReadRootFileEntryAsync(RepositoryTestFixture fixture, RelativePath path)
    {
        var snapshot = await fixture.Snapshot.ResolveAsync();
        snapshot.ShouldNotBeNull();

        IReadOnlyList<FileTreeEntry> entries = await fixture.FileTreeService.ReadAsync(snapshot.RootHash);

        if (path.Parent is not { } parentPath)
            return entries.OfType<FileEntry>().Single(entry => entry.Name == path.Name);

        foreach (var segment in parentPath.Segments)
        {
            var directory = entries.OfType<DirectoryEntry>().Single(entry => entry.Name == segment);
            entries = await fixture.FileTreeService.ReadAsync(directory.FileTreeHash);
        }

        return entries.OfType<FileEntry>().Single(entry => entry.Name == path.Name);
    }

    private static async Task<ArchiveResult> ArchiveAsync(RepositoryTestFixture fixture,
        BlobTier uploadTier,
        bool removeLocal = false,
        bool noPointers = false,
        long? smallFileThreshold = null,
        Func<ChunkHash, long, IProgress<long>>? createUploadProgress = null,
        Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>>? openStagingSession = null,
        CancellationToken cancellationToken = default)
    {
        var stagingSessionFactory = openStagingSession ?? OpenStagingSessionAsync;

        var handler = fixture.CreateArchiveHandler(stagingSessionFactory);

        return await handler.Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = uploadTier,
                SmallFileThreshold = smallFileThreshold ?? 1024 * 1024,
                RemoveLocal = removeLocal,
                NoPointers = noPointers,
                CreateUploadProgress = createUploadProgress,
            }),
            cancellationToken);


        static async Task<IFileTreeStagingSession> OpenStagingSessionAsync(LocalDirectory path, CancellationToken ct)
            => await FileTreeStagingSession.OpenAsync(path, ct);
    }

    private static ChunkHash ComputeTarHash(RepositoryTestFixture fixture, ContentHash contentHash, byte[] content)
    {
        using var tarStream = new MemoryStream();
        using (var writer = new TarWriter(tarStream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, contentHash.ToString())
            {
                DataStream = new MemoryStream(content, writable: false)
            };

            writer.WriteEntry(entry);
        }

        return ChunkHash.Parse(fixture.Encryption.ComputeHash(tarStream.ToArray()));
    }

    private sealed class RecordingChunkStorageService(
        Func<ChunkHash, Stream, long, BlobTier, IProgress<long>?, CancellationToken, Task<ChunkUploadResult>>? uploadLargeAsync = null,
        Func<ChunkHash, Stream, long, BlobTier, IProgress<long>?, CancellationToken, Task<ChunkUploadResult>>? uploadTarAsync = null,
        Func<ContentHash, ChunkHash, long, long, CancellationToken, Task<bool>>? uploadThinAsync = null) : IChunkStorageService
    {
        public Task<ChunkUploadResult> UploadLargeAsync(ChunkHash chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
            => uploadLargeAsync is not null
                ? uploadLargeAsync(chunkHash, content, sourceSize, tier, progress, cancellationToken)
                : throw new NotSupportedException();

        public Task<ChunkUploadResult> UploadTarAsync(ChunkHash chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
            => uploadTarAsync is not null
                ? uploadTarAsync(chunkHash, content, sourceSize, tier, progress, cancellationToken)
                : throw new NotSupportedException();

        public Task<bool> UploadThinAsync(ContentHash contentHash, ChunkHash parentChunkHash, long originalSize, long compressedSize, CancellationToken cancellationToken = default)
            => uploadThinAsync is not null
                ? uploadThinAsync(contentHash, parentChunkHash, originalSize, compressedSize, cancellationToken)
                : throw new NotSupportedException();

        public Task<Stream> DownloadAsync(ChunkHash chunkHash, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChunkHydrationStatus> GetHydrationStatusAsync(ChunkHash chunkHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task StartRehydrationAsync(ChunkHash chunkHash, RehydratePriority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
