using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Pipeline.Fakes;
using Arius.Tests.Shared.Fixtures;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

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
        var logger = new FakeLogger<ArchiveCommandHandler>();
        return new ArchiveCommandHandler(
            blobService, encryption, index, new ChunkStorageService(blobService, encryption), new FileTreeService(blobService, encryption, Account, containerName), new SnapshotService(blobService, encryption, Account, containerName), mediator,
            logger,
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
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("file1.bin"), content1, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("file2.bin"), content2, CancellationToken.None);

        // ── Run 1: crash after the first counted upload completion
        // Large uploads run in parallel, so the fault can leave multiple chunk bodies
        // present before any metadata write becomes visible.
        var faultingService = new FaultingBlobService(fix.BlobContainer, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobContainer, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveCommandOptions
        {
            RootDirectory = fix.LocalDirectory.ToString(),
            UploadTier    = BlobTier.Hot,
        };

        // First run crashes — expect an exception
        var r1 = await handler1.Handle(new ArchiveCommand(opts), default);
        r1.Success.ShouldBeFalse(); // pipeline should surface the fault

        // After the crash, at least one chunk body should be present.
        // The large-file path writes metadata in a separate call after OpenWrite completes,
        // so a concurrent fault can leave uploaded chunk bodies without arius-type metadata.
        var blobs = new List<RelativePath>();
        await foreach (var item in fix.BlobContainer.ListAsync(BlobPaths.ChunksPrefix))
            blobs.Add(item.Name);
        blobs.ShouldNotBeEmpty("at least one chunk should have been uploaded before the crash");

        // ── Run 2: use real service → should complete, deduplicate already-uploaded chunk
        var r2 = await fix.ArchiveAsync(opts);
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore and verify both files
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("file1.bin")).ShouldBe(content1);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("file2.bin")).ShouldBe(content2);
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
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small1.txt"), c1, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small2.txt"), c2, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small3.txt"), c3, CancellationToken.None);

        // Crash after the tar chunk upload completes but before thin chunks are written.
        // Completed upload #1 = tar chunk metadata write; then the next completed upload faults.
        var faultingService = new FaultingBlobService(fix.BlobContainer, throwAfterN: 1);
        var faultingIndex   = new ChunkIndexService(fix.BlobContainer, fix.Encryption, Account, fix.Container.Name);
        var handler1 = MakeArchiveHandler(faultingService, fix.Encryption, faultingIndex, fix.Container.Name);

        var opts = new ArchiveCommandOptions
        {
            RootDirectory = fix.LocalDirectory.ToString(),
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

        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small1.txt")).ShouldBe(c1);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small2.txt")).ShouldBe(c2);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small3.txt")).ShouldBe(c3);
    }

    // ── 15.3: Crash after all uploads but before index ────────────────────────

    [Test]
    public async Task Archive_CrashAfterAllUploadsBeforeIndex_Rerun_IndexCorrect()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Two files, one large, one small
        var large = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(large);
        var small = new byte[100];              Random.Shared.NextBytes(small);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("large.bin"), large, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small.txt"), small, CancellationToken.None);

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
            RootDirectory = fix.LocalDirectory.ToString(),
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
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("large.bin")).ShouldBe(large);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small.txt")).ShouldBe(small);
    }
}
