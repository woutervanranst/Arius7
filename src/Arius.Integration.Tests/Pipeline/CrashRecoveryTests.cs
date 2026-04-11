using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

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
    private static ArchiveCommandHandler MakeArchiveHandler(
        IBlobContainerService blobService,
        IEncryptionService  encryption,
        ChunkIndexService   index,
        string              containerName)
    {
        var mediator = Substitute.For<IMediator>();
        return new ArchiveCommandHandler(
            blobService, encryption, index, new ChunkStorageService(blobService, encryption), new FileTreeService(blobService, encryption, index, Account, containerName), new SnapshotService(blobService, encryption, Account, containerName), mediator,
            NullLogger<ArchiveCommandHandler>.Instance,
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

        // ── Run 1: crash after 1 upload (partial — uploads only one chunk)
        var faultingService = new FaultingBlobService(fix.BlobContainer, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobContainer, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveCommandOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };

        // First run crashes — expect an exception
        var r1 = await handler1.Handle(new ArchiveCommand(opts), default);
        r1.Success.ShouldBeFalse(); // pipeline should surface the fault

        // After the crash, at least one completed chunk should be present with arius-type set.
        var blobs = new List<string>();
        await foreach (var name in fix.BlobContainer.ListAsync("chunks/"))
            blobs.Add(name);
        blobs.ShouldNotBeEmpty("at least one chunk should have been uploaded before the crash");

        var metadataStates = new List<BlobMetadata>();
        foreach (var blobName in blobs)
            metadataStates.Add(await fix.BlobContainer.GetMetadataAsync(blobName));

        metadataStates.ShouldContain(
            meta => meta.Exists && meta.Metadata.ContainsKey(BlobMetadataKeys.AriusType),
            "arius-type is the crash-recovery signal");

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

        // Crash after the tar chunk upload completes but before thin chunks are written.
        // Completed upload #1 = tar chunk metadata write; then the next completed upload faults.
        var faultingService = new FaultingBlobService(fix.BlobContainer, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobContainer, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveCommandOptions
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

        // Allow the large chunk upload to complete and the thin chunk upload to complete,
        // then crash when the chunk-index shard is uploaded.
        //   completed upload #1 = SetMetadataAsync for large.bin
        //   completed upload #2 = UploadAsync for small.txt thin chunk
        //   completed upload #3 = UploadAsync for the index shard  ← crash here
        var faultingService = new FaultingBlobService(fix.BlobContainer, throwAfterN: 2);
        var faultingIndex   = new ChunkIndexService(fix.BlobContainer, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveCommandOptions
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
