using System.Formats.Tar;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.LocalFile;
using Arius.Core.Shared.Storage;
using Mediator;
using NSubstitute;

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

        await env.Blobs.SeedLargeBlobAsync(BlobPaths.Chunk(chunkHash), content, uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(chunkHash));

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
        await env.Blobs.SeedTarBlobAsync(BlobPaths.Chunk(tarHash), [content], uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(tarHash));

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
        var blobName = BlobPaths.Chunk(ChunkHash.Parse(contentHash));

        await env.Blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        env.Blobs.ClearMetadata(blobName);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await env.ArchiveAsync(BlobTier.Archive);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Blobs.DeletedBlobNames.ShouldContain(blobName);

        var finalMeta = await env.Blobs.GetMetadataAsync(blobName);
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
    public async Task Archive_CaseInsensitivePathCollision_FailsBeforeSnapshotPublication()
    {
        using var env = new ArchiveTestEnvironment();

        var result = await env.ArchiveAsync(
            BlobTier.Cool,
            enumerateFilePairs: _ =>
            [
                new FilePair
                {
                    Path = RelativePath.Parse("photos/pic.jpg"),
                    Binary = new BinaryFile
                    {
                        Path = RelativePath.Parse("photos/pic.jpg"),
                        Size = 32,
                        Created = DateTimeOffset.UnixEpoch,
                        Modified = DateTimeOffset.UnixEpoch
                    },
                    Pointer = null
                },
                new FilePair
                {
                    Path = RelativePath.Parse("Photos/pic.jpg"),
                    Binary = new BinaryFile
                    {
                        Path = RelativePath.Parse("Photos/pic.jpg"),
                        Size = 32,
                        Created = DateTimeOffset.UnixEpoch,
                        Modified = DateTimeOffset.UnixEpoch
                    },
                    Pointer = null
                }
            ]);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("case-insensitive", Case.Insensitive);
        result.ErrorMessage.ShouldContain("photos/pic.jpg");
        result.ErrorMessage.ShouldContain("Photos/pic.jpg");
        result.RootHash.ShouldBeNull();
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
    public async Task Archive_UsesTypedBinaryMetadataForScanAndHashProgress()
    {
        using var env = new ArchiveTestEnvironment();

        var mediator = env.Mediator;
        var scannedEvents = new List<FileScannedEvent>();
        var hashingEvents = new List<FileHashingEvent>();
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

        var content = env.WriteRandomFile("photos/pic.jpg", 32);
        var result = await env.ArchiveAsync(
            BlobTier.Cool,
            enumerateFilePairs: _ =>
            [
                new FilePair
                {
                    Path = RelativePath.Parse("photos/pic.jpg"),
                    Binary = new BinaryFile
                    {
                        Path = RelativePath.Parse("photos/pic.jpg"),
                        Size = 1234,
                        Created = DateTimeOffset.UnixEpoch,
                        Modified = DateTimeOffset.UnixEpoch
                    },
                    Pointer = null
                }
            ]);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        scannedEvents.ShouldHaveSingleItem();
        scannedEvents[0].RelativePath.ShouldBe("photos/pic.jpg");
        scannedEvents[0].FileSize.ShouldBe(1234);

        hashingEvents.ShouldHaveSingleItem();
        hashingEvents[0].RelativePath.ShouldBe("photos/pic.jpg");
        hashingEvents[0].FileSize.ShouldBe(1234);
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

        await using var stagingSession = await FileTreeStagingSession.OpenAsync(env.FileTreeCacheDirectory);

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
