using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
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
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("large.bin", 2 * 1024 * 1024);
        var contentHash = env.Encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);

        await env.Blobs.SeedLargeBlobAsync(BlobPathStrings.Chunk(chunkHash), content, uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPathStrings.Chunk(chunkHash));

        var result = await env.ArchiveAsync(uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Lookup(contentHash).ShouldNotBeNull();
    }

    [Test]
    [MatrixDataSource]
    public async Task Archive_TarBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("small.txt", 256);
        var contentHash = env.Encryption.ComputeHash(content);

        var tarHash = ComputeTarHash(env, contentHash, content);
        await env.Blobs.SeedTarBlobAsync(BlobPathStrings.Chunk(tarHash), [content], uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPathStrings.Chunk(tarHash));

        var result = await env.ArchiveAsync(uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Lookup(contentHash).ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_LargeBlobWithoutMetadata_Rerun_DeletesAndRetries()
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("partial.bin", 2 * 1024 * 1024);
        var contentHash = env.Encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);
        var blobName = BlobPaths.ChunkPath(chunkHash);

        await env.Blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        env.Blobs.ClearMetadata(blobName);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await env.ArchiveAsync(BlobTier.Archive);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Blobs.DeletedBlobNames.ShouldContain(blobName);

        var finalMeta = await env.Blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash));
        finalMeta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    [Test]
    public async Task Archive_NewContent_CreatesSnapshotWithRootHash()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.RootHash.ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_NewContent_EmitsConsistentPhaseTimingLogs()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var messages = env.ArchiveLogs
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
        using var env = new ArchiveTestEnvironment();
        var relativePath = RelativePath.Parse("docs/readme.txt");
        var created = new DateTimeOffset(2021, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var modified = new DateTimeOffset(2022, 5, 6, 7, 8, 9, TimeSpan.Zero);

        env.WriteRandomFile(relativePath.ToString(), 128);
        env.SetTimestamps(relativePath, created, modified);

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var entry = await env.ReadRootFileEntryAsync(relativePath);

        if (!OperatingSystem.IsLinux())
            entry.Created.ShouldBe(created);

        entry.Modified.ShouldBe(modified);
    }

    [Test]
    public async Task Archive_UsesEnumeratedBinaryMetadataForScanAndHashProgress()
    {
        using var env = new ArchiveTestEnvironment();

        var mediator = env.Mediator;
        var scannedEvents = new ConcurrentBag<FileScannedEvent>();
        var hashingEvents = new ConcurrentBag<FileHashingEvent>();
        mediator
            .When(x => x.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                switch (callInfo.ArgAt<INotification>(0))
                {
                    case FileScannedEvent scanned:
                        scannedEvents.Add(scanned);
                        break;
                    case FileHashingEvent hashing:
                        hashingEvents.Add(hashing);
                        break;
                }
            });

        env.WriteRandomFile("photos/pic.jpg", 32);
        var result = await env.ArchiveAsync(BlobTier.Cool);

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
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 128);

        var result = await env.ArchiveAsync(BlobTier.Cool, removeLocal: true);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        File.Exists(Path.Combine(env.RootDirectory, "docs", "readme.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(env.RootDirectory, "docs", "readme.txt.pointer.arius")).ShouldBeTrue();
    }

    [Test]
    public async Task Archive_RemoveLocalAndNoPointers_ReturnsValidationFailure()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 128);

        var result = await env.ArchiveAsync(BlobTier.Cool, removeLocal: true, noPointers: true);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("--remove-local cannot be combined with --no-pointers");
    }

    [Test]
    public async Task Archive_WhenAnotherLocalRunHoldsStagingLock_FailsFast()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        await using var stagingSession = await FileTreeStagingSession.OpenAsync(LocalDirectory.Parse(env.FileTreeCacheDirectory));

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("staging", Case.Insensitive);
        result.ErrorMessage.ShouldContain("already open", Case.Insensitive);
    }

    [Test]
    public async Task Archive_WhenCancelledBeforeOpeningStagingSession_PropagatesCancellation()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await env.ArchiveAsync(BlobTier.Cool, cts.Token));
    }

    [Test]
    public async Task Archive_WhenOpeningStagingSessionThrowsNonIoException_ReturnsFailedResult()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        var result = await env.ArchiveAsync(
            BlobTier.Cool,
            openStagingSession: (_, _) => throw new InvalidOperationException("staging setup failed"));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("staging setup failed");
    }

    private static ChunkHash ComputeTarHash(ArchiveTestEnvironment env, ContentHash contentHash, byte[] content)
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

        return ChunkHash.Parse(env.Encryption.ComputeHash(tarStream.ToArray()));
    }
}
