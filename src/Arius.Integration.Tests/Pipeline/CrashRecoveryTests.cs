using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

// ── Fault-injection blob service wrapper ──────────────────────────────────────

/// <summary>
/// Wraps an <see cref="IBlobStorageService"/> and throws after <paramref name="throwAfterN"/>
/// successful upload calls. Used to simulate crashes mid-pipeline.
/// </summary>
internal sealed class FaultingBlobService(IBlobStorageService inner, int throwAfterN)
    : IBlobStorageService
{
    private int _uploadCount;

    public async Task UploadAsync(
        string blobName, Stream content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier tier, string? contentType = null, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _uploadCount);
        if (count > throwAfterN)
            throw new IOException($"Fault-injected failure on upload #{count}");
        await inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);
    }

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
        => inner.DownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
        => inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => inner.ListAsync(prefix, cancellationToken);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
        => inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => inner.SetTierAsync(blobName, tier, cancellationToken);

    public async Task<Stream> OpenWriteAsync(string blobName, string? contentType = null,
        bool throwOnExists = false, CancellationToken cancellationToken = default)
        => await inner.OpenWriteAsync(blobName, contentType, throwOnExists, cancellationToken);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier,
        RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(blobName, cancellationToken);
}

// ── Crash recovery tests ──────────────────────────────────────────────────────

/// <summary>
/// Crash recovery integration tests: fault-injection → incomplete archive → re-run → correct snapshot.
///
/// Covers tasks 9.4, 9.5, 15.1, 15.2, 15.3.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class CrashRecoveryTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";

    /// <summary>
    /// Creates a handler with the given (possibly faulting) blob service.
    /// Uses the same encryption and index as the fixture.
    /// </summary>
    private static ArchivePipelineHandler MakeArchiveHandler(
        IBlobStorageService blobService,
        IEncryptionService  encryption,
        ChunkIndexService   index,
        string              containerName)
    {
        var mediator = Substitute.For<IMediator>();
        return new ArchivePipelineHandler(
            blobService, encryption, index, mediator,
            NullLogger<ArchivePipelineHandler>.Instance,
            Account, containerName);
    }

    // ── 9.4 / 15.1: Crash after N large-file uploads ─────────────────────────

    [Test]
    public async Task Archive_CrashAfterFirstUpload_Rerun_ProducesCorrectSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Two large files (> 1 MB each)
        var content1 = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(content1);
        var content2 = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(content2);
        fix.WriteFile("file1.bin", content1);
        fix.WriteFile("file2.bin", content2);

        // ── Run 1: crash during UploadAsync #2 (index/snapshot upload)
        // Both large-file chunks complete via OpenWriteAsync (not fault-counted) and
        // receive their AriusType metadata before UploadAsync #2 fires.
        var faultingService = new FaultingBlobService(fix.BlobStorage, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobStorage, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };

        // First run crashes — expect an exception
        var r1 = await handler1.Handle(new ArchiveCommand(opts), default);
        r1.Success.ShouldBeFalse(); // pipeline should surface the fault

        // After the crash, the first blob should be present with arius-type set (crash-recovery signal)
        var blobs = new List<string>();
        await foreach (var name in fix.BlobStorage.ListAsync("chunks/"))
            blobs.Add(name);
        blobs.ShouldNotBeEmpty("at least one chunk should have been uploaded before the crash");
        var firstMeta = await fix.BlobStorage.GetMetadataAsync(blobs[0]);
        firstMeta.Exists.ShouldBeTrue();
        firstMeta.Metadata.ContainsKey(BlobMetadataKeys.AriusType).ShouldBeTrue("arius-type is the crash-recovery signal");

        // ── Run 2: use real service → should complete, deduplicate already-uploaded chunk
        var r2 = await fix.ArchiveAsync(opts);
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore and verify both files
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.ReadRestored("file1.bin").ShouldBe(content1);
        fix.ReadRestored("file2.bin").ShouldBe(content2);
    }

    // ── 9.5 / 15.2: Crash after tar upload, before thin chunks ───────────────

    [Test]
    public async Task Archive_CrashAfterTarBeforeThinChunks_Rerun_CorrectSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Three small files → will all go into one tar bundle
        var c1 = new byte[100]; Random.Shared.NextBytes(c1);
        var c2 = new byte[200]; Random.Shared.NextBytes(c2);
        var c3 = new byte[300]; Random.Shared.NextBytes(c3);
        fix.WriteFile("small1.txt", c1);
        fix.WriteFile("small2.txt", c2);
        fix.WriteFile("small3.txt", c3);

        // FaultingBlobService counts only UploadAsync calls (not OpenWriteAsync).
        // The tar blob is uploaded via OpenWriteAsync (not counted) and completes with AriusType.
        // throwAfterN=1: thin chunk #1 (UploadAsync #1) succeeds, thin chunk #2 throws.
        var faultingService = new FaultingBlobService(fix.BlobStorage, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobStorage, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };

        var r1 = await handler1.Handle(new ArchiveCommand(opts), default);
        r1.Success.ShouldBeFalse();

        // ── Re-run with normal service
        var r2 = await fix.ArchiveAsync(opts);
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(3);

        fix.ReadRestored("small1.txt").ShouldBe(c1);
        fix.ReadRestored("small2.txt").ShouldBe(c2);
        fix.ReadRestored("small3.txt").ShouldBe(c3);
    }

    // ── 15.3: Crash after all uploads but before index ────────────────────────

    [Test]
    public async Task Archive_CrashAfterAllUploadsBeforeIndex_Rerun_IndexCorrect()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Two files, one large, one small
        var large = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(large);
        var small = new byte[100];              Random.Shared.NextBytes(small);
        fix.WriteFile("large.bin", large);
        fix.WriteFile("small.txt", small);

        // FaultingBlobService counts only UploadAsync. With 1 large + 1 small file:
        //   #1 thin chunk, #2 tree node — both succeed; #3 snapshot → THROWS (3 > 2).
        // All data chunks are fully uploaded (via OpenWriteAsync, not counted) before crash.
        var faultingService = new FaultingBlobService(fix.BlobStorage, throwAfterN: 2);
        var faultingIndex   = new ChunkIndexService(fix.BlobStorage, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };

        var r1 = await handler1.Handle(new ArchiveCommand(opts), default);
        r1.Success.ShouldBeFalse();

        // ── Re-run: all chunks already uploaded, only index + snapshot uploaded
        var r2 = await fix.ArchiveAsync(opts);
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);
        fix.ReadRestored("large.bin").ShouldBe(large);
        fix.ReadRestored("small.txt").ShouldBe(small);
    }
}
