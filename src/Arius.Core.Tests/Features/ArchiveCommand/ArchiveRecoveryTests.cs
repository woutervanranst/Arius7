using System.Formats.Tar;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging.Testing;

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
    public async Task Archive_NewContent_EmitsTailPhaseTimingLogs()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 1024);

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var messages = env.ArchiveLogs
            .GetSnapshot(clear: false)
            .Select(static record => record.Message)
            .Where(static message => message.Contains("[phase] tail", StringComparison.Ordinal))
            .ToArray();

        messages.ShouldContain(message => message.Contains("[phase] tail validate-filetrees", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail flush-index", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail build-filetree", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail snapshot", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail write-pointers", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail delete-local", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[phase] tail complete", StringComparison.Ordinal));
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
