using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Mediator;
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
        (await fixture.Index.LookupAsync(contentHash)).ShouldNotBeNull();
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
        (await fixture.Index.LookupAsync(contentHash)).ShouldNotBeNull();
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

        messages.ShouldContain(message => message.Contains("[phase] ensure-container", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] open-staging", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] enumerate", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] hash", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] dedup-route", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] large-upload", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tar-build", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tar-upload", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] await-workers", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] validate-filetrees", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] flush-index", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] build-filetree", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] snapshot", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] write-pointers", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] delete-local", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] complete", StringComparison.Ordinal));
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
}
