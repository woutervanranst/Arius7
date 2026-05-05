using System.Formats.Tar;
using System.IO.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

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
    public async Task Archive_TypedRelativePath_UsesCanonicalPathTextAtCurrentStringBoundaries()
    {
        using var env = new ArchiveTestEnvironment();
        env.WriteRandomFile("docs/readme.txt", 2 * 1024 * 1024);

        var result = await env.ArchiveAsync(BlobTier.Cool);

        result.Success.ShouldBeTrue(result.ErrorMessage);

        var messages = env.ArchiveLogs
            .GetSnapshot(clear: false)
            .Select(static record => record.Message)
            .ToArray();

        messages.ShouldContain(message => message.Contains("[hash] docs/readme.txt ->", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[dedup] docs/readme.txt -> new/", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("[upload] Start: docs/readme.txt", StringComparison.Ordinal));
        messages.ShouldNotContain(message => message.Contains("docs\\readme.txt", StringComparison.Ordinal));

        var snapshotBlobName = env.Blobs.UploadedBlobNames
            .Single(name => name.StartsWith(BlobPaths.Snapshots, StringComparison.Ordinal));

        var snapshot = await ReadSnapshotAsync(env, snapshotBlobName);
        var rootEntries = await ReadFileTreeAsync(env, snapshot.RootHash);
        var docsDirectory = rootEntries.OfType<DirectoryEntry>().Single(entry => entry.Name == SegmentOf("docs"));
        var docsEntries = await ReadFileTreeAsync(env, docsDirectory.FileTreeHash);

        docsEntries.OfType<FileEntry>().Single(entry => entry.Name == SegmentOf("readme.txt"));
        rootEntries.OfType<FileEntry>().ShouldNotContain(entry => entry.Name.ToString().Contains('\\'));
        docsEntries.OfType<FileEntry>().ShouldNotContain(entry => entry.Name.ToString().Contains('\\'));
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

    private static async Task<SnapshotManifest> ReadSnapshotAsync(ArchiveTestEnvironment env, string snapshotBlobName)
    {
        await using var stream = await env.Blobs.DownloadAsync(snapshotBlobName);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return await SnapshotSerializer.DeserializeAsync(memory.ToArray(), env.Encryption);
    }

    private static async Task<IReadOnlyList<FileTreeEntry>> ReadFileTreeAsync(ArchiveTestEnvironment env, FileTreeHash hash)
    {
        await using var stream = await env.Blobs.DownloadAsync(BlobPaths.FileTree(hash));
        await using var decrypted = env.Encryption.WrapForDecryption(stream);
        await using var gzip = new GZipStream(decrypted, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        await gzip.CopyToAsync(memory);
        return FileTreeSerializer.Deserialize(memory.ToArray());
    }
}
