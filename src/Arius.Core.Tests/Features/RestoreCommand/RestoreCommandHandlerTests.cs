using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreCommandHandlerTests
{
    static void AssertRestoredFile(RepositoryTestFixture fixture, string relativePath, byte[] expectedContent, DateTime expectedCreated, DateTime expectedModified)
    {
        var restoreRelativePath = RelativePath.Parse(relativePath);
        var restoredTimestamps = fixture.RestoreFileSystem.GetTimestamps(restoreRelativePath);
        var pointerTimestamps = fixture.RestoreFileSystem.GetTimestamps(restoreRelativePath.ToPointerPath());

        fixture.RestoreFileSystem.ReadAllBytes(restoreRelativePath).ShouldBe(expectedContent);
        fixture.RestoreFileSystem.FileExists(restoreRelativePath.ToPointerPath()).ShouldBeTrue($"Pointer file should exist for {relativePath}");

        if (!OperatingSystem.IsLinux())
        {
            restoredTimestamps.Created.UtcDateTime.ShouldBe(expectedCreated, $"Binary CreationTimeUtc for {relativePath}");
            pointerTimestamps.Created.UtcDateTime.ShouldBe(expectedCreated, $"Pointer CreationTimeUtc for {relativePath}");
        }

        restoredTimestamps.Modified.UtcDateTime.ShouldBe(expectedModified, $"Binary LastWriteTimeUtc for {relativePath}");
        pointerTimestamps.Modified.UtcDateTime.ShouldBe(expectedModified, $"Pointer LastWriteTimeUtc for {relativePath}");
    }

    [Test]
    public async Task Handle_Restores_File_Written_Through_TypedFixtureFileSystems()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-typed-fixture-restore-{Guid.NewGuid():N}",
            $"ctr-typed-fixture-restore-{Guid.NewGuid():N}");

        var relativePath = RelativePath.Parse("nested/file.bin");
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, [1, 2, 3], CancellationToken.None);

        fixture.LocalFileSystem.FileExists(relativePath).ShouldBeTrue();

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fixture.RestoreFileSystem.FileExists(relativePath).ShouldBeTrue();
        fixture.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe([1, 2, 3]);
        fixture.RestoreFileSystem.FileExists(relativePath).ShouldBeTrue();
    }

    [Test]
    public async Task Restore_AvailableContent_EmitsConsistentPhaseTimingLogs()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-phase-{Guid.NewGuid():N}",
            $"ctr-restore-phase-{Guid.NewGuid():N}");

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("docs/readme.txt"), [1, 2, 3], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        var messages = fixture.RestoreLogs
            .GetSnapshot(clear: false)
            .Select(static record => record.Message)
            .ToArray();

        var phases = messages
            .Where(static message => message.StartsWith("[phase] ", StringComparison.Ordinal))
            .Select(static message => message[8..])
            .ToArray();

        phases.ShouldBe([
            "resolve-snapshot",
            "classify",
            "download"
        ]);
        messages.ShouldContain(static message => message.StartsWith("[restore] Start:", StringComparison.Ordinal));
        messages.ShouldContain(static message => message.StartsWith("[restore] Done:", StringComparison.Ordinal));
        messages.ShouldNotContain(static message => message.Contains("TODO", StringComparison.Ordinal));
    }

    [Test]
    public async Task Handle_ArchivedContent_EmitsConfirmAndRehydratePhaseLogs()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-rehydrate-phase-{Guid.NewGuid():N}",
            $"ctr-restore-rehydrate-phase-{Guid.NewGuid():N}");

        var content = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("archive/file.bin"), content, CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Archive,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
                ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        var messages = fixture.RestoreLogs
            .GetSnapshot(clear: false)
            .Select(static record => record.Message)
            .ToArray();

        var phases = messages
            .Where(static message => message.StartsWith("[phase] ", StringComparison.Ordinal))
            .Select(static message => message[8..])
            .ToArray();

        phases.ShouldBe([
            "resolve-snapshot",
            "classify",
            "confirm-rehydration",
            "rehydrate"
        ]);
        messages.ShouldContain(static message => message.StartsWith("[rehydration] Requested:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var accountName   = $"acct-restore-missing-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-missing-{Guid.NewGuid():N}";

        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            var       blobs           = new ThrowOnCreateBlobContainerService("restore");
            var       snapshotSvc     = new SnapshotService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);
            using var index           = new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, snapshotSvc, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);
            var       mediator        = Substitute.For<IMediator>();
            var       logger          = new FakeLogger<RestoreCommandHandler>();

            var handler = new RestoreCommandHandler(IEncryptionService.PlaintextInstance, index, new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), fileTreeService, snapshotSvc, mediator, logger, accountName, containerName);

            var result = await handler.Handle(
                new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
                    {
                        RootDirectory = Path.GetTempPath()
                    }),
                CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.ErrorMessage.ShouldBe("No snapshots found in this repository.");
            blobs.CreateCalled.ShouldBeFalse();
        }
        finally
        {
            RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);
        }
    }

    [Test]
    public async Task Handle_Restores_DuplicateLargeChunkContent_ToAllPaths()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-duplicates-{Guid.NewGuid():N}",
            $"ctr-restore-duplicates-{Guid.NewGuid():N}");

        var content = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("archives/duplicates/binary-a.bin"), content, CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("nested/deep/a/b/c/binary-b.bin"),   content, CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);
        fixture.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("archives/duplicates/binary-a.bin")).ShouldBe(content);
        fixture.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("nested/deep/a/b/c/binary-b.bin")).ShouldBe(content);

        await fixture.Mediator.Received(1).Publish(
            Arg.Is<FileRestoredEvent>(e => e.RelativePath == RelativePath.Parse("archives/duplicates/binary-a.bin") && e.FileSize == content.Length),
            Arg.Any<CancellationToken>());
        await fixture.Mediator.Received(1).Publish(
            Arg.Is<FileRestoredEvent>(e => e.RelativePath == RelativePath.Parse("nested/deep/a/b/c/binary-b.bin") && e.FileSize == content.Length),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Restores_DuplicateTarEntryContent_ToAllPaths_WithPerPathMetadata()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-tar-duplicates-{Guid.NewGuid():N}",
            $"ctr-restore-tar-duplicates-{Guid.NewGuid():N}");

        var content = new byte[512 * 1024];
        Random.Shared.NextBytes(content);

        var firstCreated   = new DateTime(2021, 4, 5, 6, 7,  8,  DateTimeKind.Utc);
        var firstModified  = new DateTime(2022, 5, 6, 7, 8,  9,  DateTimeKind.Utc);
        var secondCreated  = new DateTime(2023, 6, 7, 8, 9,  10, DateTimeKind.Utc);
        var secondModified = new DateTime(2024, 7, 8, 9, 10, 11, DateTimeKind.Utc);

        var firstPath = RelativePath.Parse("archives/duplicates/copy-a.bin");
        var secondPath = RelativePath.Parse("nested/deep/a/b/c/copy-b.bin");
        await fixture.LocalFileSystem.WriteAllBytesAsync(firstPath, content, CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(secondPath, content, CancellationToken.None);
        fixture.LocalFileSystem.SetTimestamps(firstPath, new DateTimeOffset(firstCreated), new DateTimeOffset(firstModified));
        fixture.LocalFileSystem.SetTimestamps(secondPath, new DateTimeOffset(secondCreated), new DateTimeOffset(secondModified));

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        AssertRestoredFile(fixture, "archives/duplicates/copy-a.bin", content, firstCreated,  firstModified);
        AssertRestoredFile(fixture, "nested/deep/a/b/c/copy-b.bin",   content, secondCreated, secondModified);

    }

    [Test]
    public async Task Handle_Restores_DuplicateZeroByteTarEntryContent_ToAllPaths_WithPerPathMetadata()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-zero-tar-duplicates-{Guid.NewGuid():N}",
            $"ctr-restore-zero-tar-duplicates-{Guid.NewGuid():N}");

        var firstCreated   = new DateTime(2020, 1, 2, 3, 4,  5, DateTimeKind.Utc);
        var firstModified  = new DateTime(2020, 2, 3, 4, 5,  6, DateTimeKind.Utc);
        var secondCreated  = new DateTime(2020, 3, 4, 5, 6,  7, DateTimeKind.Utc);
        var secondModified = new DateTime(2020, 4, 5, 6, 7,  8, DateTimeKind.Utc);

        var firstPath = RelativePath.Parse("zero/a.txt");
        var secondPath = RelativePath.Parse("zero/b.txt");
        await fixture.LocalFileSystem.WriteAllBytesAsync(firstPath, Array.Empty<byte>(), CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(secondPath, Array.Empty<byte>(), CancellationToken.None);
        fixture.LocalFileSystem.SetTimestamps(firstPath, new DateTimeOffset(firstCreated), new DateTimeOffset(firstModified));
        fixture.LocalFileSystem.SetTimestamps(secondPath, new DateTimeOffset(secondCreated), new DateTimeOffset(secondModified));

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        AssertRestoredFile(fixture, "zero/a.txt", Array.Empty<byte>(), firstCreated,  firstModified);
        AssertRestoredFile(fixture, "zero/b.txt", Array.Empty<byte>(), secondCreated, secondModified);
    }

    [Test]
    public async Task Handle_InvalidSnapshotContentHash_FailsRestore()
    {
        var blobs         = new FakeSeededBlobContainerService();
        var mediator      = Substitute.For<IMediator>();
        var accountName   = $"acct-restore-invalid-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-invalid-{Guid.NewGuid():N}";
        var restoreRootDirectory = TestTempRoots.CreateDirectory("restore-invalid");
        var restoreFileSystem = new RelativeFileSystem(restoreRootDirectory);

        restoreFileSystem.CreateDirectory(RelativePath.Root);
        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            var       snapshotSvc     = new SnapshotService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);
            using var index           = new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, snapshotSvc, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);

            var rootHash = FileTreeHash.Parse(IEncryptionService.PlaintextInstance.ComputeHash("root-broken"u8).ToString());
            var snapshot = new SnapshotManifest
            {
                Timestamp    = DateTimeOffset.UtcNow,
                RootHash     = rootHash,
                FileCount    = 2,
                TotalSize    = 2,
                AriusVersion = "test"
            };

            var validHash = ContentHash.Parse(IEncryptionService.PlaintextInstance.ComputeHash("healthy"u8).ToString());
            var chunkHash = ChunkHash.Parse(validHash);
            index.AddEntry(new ShardEntry(validHash, chunkHash, OriginalSize: 7, ChunkSize: 7, BlobTier.Cool));

            var invalidTreePayload = System.Text.Encoding.UTF8.GetBytes($"not-a-hash F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} broken.txt\n{validHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} healthy.txt\n");
            blobs.AddBlob(BlobPaths.FileTreePath(rootHash),             await CompressAsync(invalidTreePayload));
            blobs.AddBlob(BlobPaths.ChunkPath(chunkHash),               await CompressAsync("healthy"u8.ToArray()));
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

            var handler = new RestoreCommandHandler(IEncryptionService.PlaintextInstance, index, new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), fileTreeService, snapshotSvc, mediator, new FakeLogger<RestoreCommandHandler>(), accountName, containerName);

            var result = await handler.Handle(new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions { RootDirectory = restoreRootDirectory.ToString(), Overwrite = true }), CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FilesRestored.ShouldBe(0);
            result.FilesSkipped.ShouldBe(0);
            result.ErrorMessage.ShouldNotBeNull();
            result.ErrorMessage.ShouldContain("not-a-hash");
            restoreFileSystem.FileExists(RelativePath.Parse("broken.txt")).ShouldBeFalse();
            restoreFileSystem.FileExists(RelativePath.Parse("healthy.txt")).ShouldBeFalse();
        }
        finally
        {
            RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

            restoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task Handle_UnresolvedSnapshotContentHash_LogsChunkIndexRepairGuidance()
    {
        var blobs         = new FakeSeededBlobContainerService();
        var mediator      = Substitute.For<IMediator>();
        var logger        = new FakeLogger<RestoreCommandHandler>();
        var accountName   = $"acct-restore-missing-index-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-missing-index-{Guid.NewGuid():N}";
        var restoreRootDirectory = TestTempRoots.CreateDirectory("restore-missing-index");
        var restoreFileSystem = new RelativeFileSystem(restoreRootDirectory);

        restoreFileSystem.CreateDirectory(RelativePath.Root);
        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            var       snapshotSvc     = new SnapshotService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);
            using var index           = new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, snapshotSvc, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);

            var rootHash = FileTreeHash.Parse(IEncryptionService.PlaintextInstance.ComputeHash("root-missing-index"u8).ToString());
            var snapshot = new SnapshotManifest
            {
                Timestamp    = DateTimeOffset.UtcNow,
                RootHash     = rootHash,
                FileCount    = 1,
                TotalSize    = 7,
                AriusVersion = "test"
            };

            var missingHash = ContentHash.Parse(IEncryptionService.PlaintextInstance.ComputeHash("missing"u8).ToString());
            var fileTreePayload = System.Text.Encoding.UTF8.GetBytes($"{missingHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} missing.txt\n");
            blobs.AddBlob(BlobPaths.FileTreePath(rootHash), await CompressAsync(fileTreePayload));
            blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

            var handler = new RestoreCommandHandler(IEncryptionService.PlaintextInstance, index, new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), fileTreeService, snapshotSvc, mediator, logger, accountName, containerName);

            var result = await handler.Handle(new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions { RootDirectory = restoreRootDirectory.ToString(), Overwrite = true }), CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.ErrorMessage.ShouldNotBeNull();
            result.ErrorMessage.ShouldContain("Run the explicit chunk-index repair command");

            var messages = logger.Collector
                .GetSnapshot(clear: false)
                .Select(static record => record.Message)
                .ToArray();

            messages.ShouldContain(static message =>
                message.StartsWith("[chunk] Chunk-index resolution failed: category=unresolved-snapshot-content;", StringComparison.Ordinal) &&
                message.Contains("Run the explicit chunk-index repair command and retry restore", StringComparison.Ordinal));
            messages.ShouldContain(static message => message.StartsWith("[restore] Failure:", StringComparison.Ordinal));
        }
        finally
        {
            RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

            restoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    [Test]
    public async Task Handle_CorruptChunkIndexShard_LogsChunkIndexRepairGuidance()
    {
        var contentHash = ContentHash.Parse(new string('a', 64));

        await AssertRestoreChunkIndexFailureLogAsync(
            contentHash,
            seedBlobs: async (blobs, rootHash, snapshot, encryption) =>
            {
                blobs.AddBlob(BlobPaths.FileTreePath(rootHash), await CompressAsync(System.Text.Encoding.UTF8.GetBytes($"{contentHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} corrupt.txt\n")));
                blobs.AddBlob(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)), [1, 2, 3]);
                blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, encryption, ICompressionService.ZtdInstance));
            },
            expectedCategory: "corrupt",
            expectedGuidance: "Run the explicit chunk-index repair command and retry restore");
    }

    [Test]
    public async Task Handle_IncompleteChunkIndexRepair_LogsRepairGuidance()
    {
        var contentHash = ContentHash.Parse(new string('b', 64));

        await AssertRestoreChunkIndexFailureLogAsync(
            contentHash,
            seedBlobs: async (blobs, rootHash, snapshot, encryption) =>
            {
                blobs.AddBlob(BlobPaths.FileTreePath(rootHash), await CompressAsync(System.Text.Encoding.UTF8.GetBytes($"{contentHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} repair-marker.txt\n")));
                blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, encryption, ICompressionService.ZtdInstance));
            },
            beforeHandle: (repositoryRoot, cancellationToken) =>
            {
                var repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
                repositoryFileSystem.CreateDirectory(RelativePath.Root);
                return repositoryFileSystem.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], cancellationToken);
            },
            expectedCategory: "repair-incomplete",
            expectedGuidance: "Rerun the explicit chunk-index repair command before retrying restore");
    }

    [Test]
    public async Task Handle_TargetPath_DoesNotMatchPartialSegment()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-target-segments-{Guid.NewGuid():N}",
            $"ctr-restore-target-segments-{Guid.NewGuid():N}");

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/pic.jpg"), [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photoshop/logo.png"), [4, 5, 6], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath = RelativePath.Parse("photos"),
                Overwrite = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/pic.jpg")).ShouldBeTrue();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photoshop/logo.png")).ShouldBeFalse();
    }

    [Test]
    public async Task Handle_RootTargetPath_RestoresFullSnapshot()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-root-target-{Guid.NewGuid():N}",
            $"ctr-restore-root-target-{Guid.NewGuid():N}");

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/pic.jpg"), [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("docs/readme.txt"), [4, 5, 6], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath = RelativePath.Root,
                Overwrite = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/pic.jpg")).ShouldBeTrue();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("docs/readme.txt")).ShouldBeTrue();
    }

    [Test]
    public async Task Handle_NoPointers_DoesNotCreatePointerFiles()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-no-pointers-{Guid.NewGuid():N}",
            $"ctr-restore-no-pointers-{Guid.NewGuid():N}");

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/pic.jpg"), [1, 2, 3], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath = RelativePath.Parse("photos/pic.jpg"),
                Overwrite = true,
                NoPointers = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/pic.jpg")).ShouldBeTrue();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/pic.jpg.pointer.arius")).ShouldBeFalse();
    }

    [Test]
    public async Task Handle_FileTarget_RestoresOnlyRequestedFile()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-file-target-{Guid.NewGuid():N}",
            $"ctr-restore-file-target-{Guid.NewGuid():N}");

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/pic.jpg"), [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/other.jpg"), [4, 5, 6], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier = BlobTier.Cool,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath = RelativePath.Parse("photos/pic.jpg"),
                Overwrite = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/pic.jpg")).ShouldBeTrue();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("photos/other.jpg")).ShouldBeFalse();
    }

    [Test]
    public async Task Handle_ScatteredTarEntries_DownloadsChunkOnce_AndRestoresAll()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-scattered-tar-{Guid.NewGuid():N}",
            $"ctr-restore-scattered-tar-{Guid.NewGuid():N}");

        // Three distinct small files at scattered tree paths — they bundle into a single tar chunk,
        // but the restore walk visits them far apart (breadth-first). The refcount-flush grouper
        // must still download the tar exactly once.
        var paths = new[]
        {
            RelativePath.Parse("aaa/first.bin"),
            RelativePath.Parse("mmm/deep/second.bin"),
            RelativePath.Parse("zzz/third.bin"),
        };
        var contents = new[] { new byte[] { 1, 1, 1 }, new byte[] { 2, 2, 2, 2 }, new byte[] { 3, 3 } };
        for (var i = 0; i < paths.Length; i++)
            await fixture.LocalFileSystem.WriteAllBytesAsync(paths[i], contents[i], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024, // all three are "small" → one tar bundle
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Isolate restore-time blob access from archive-time access.
        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        blobs.RequestedBlobNames.Clear();

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(3);

        for (var i = 0; i < paths.Length; i++)
            fixture.RestoreFileSystem.ReadAllBytes(paths[i]).ShouldBe(contents[i]);

        // The single tar chunk must be downloaded exactly once despite three scattered files.
        var chunkDownloads = blobs.RequestedBlobNames.Count(n => n.ToString().StartsWith("chunks/"));
        chunkDownloads.ShouldBe(1);
    }

    [Test]
    public async Task Handle_ArchivedTarTarget_EstimatesFullTarChunkRehydrationSize()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-tar-rehydrate-size-{Guid.NewGuid():N}",
            $"ctr-restore-tar-rehydrate-size-{Guid.NewGuid():N}");

        var targetPath = RelativePath.Parse("selected/tiny.bin");
        await fixture.LocalFileSystem.WriteAllBytesAsync(targetPath, [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("other/large-small-file.bin"), Enumerable.Repeat((byte)7, 128 * 1024).ToArray(), CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Archive,
                SmallFileThreshold = 1024 * 1024,
                TarTargetSize      = 1024 * 1024,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        var tarChunk = await FindSingleChunkBlobAsync(blobs, BlobMetadataKeys.TypeTar);
        var tarMetadata = await blobs.GetMetadataAsync(tarChunk);
        var tarChunkSize = tarMetadata.ContentLength.ShouldNotBeNull();

        RestoreCostEstimate? capturedEstimate = null;
        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath    = targetPath,
                Overwrite     = true,
                ConfirmRehydration = (estimate, _) =>
                {
                    capturedEstimate = estimate;
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        capturedEstimate.ShouldNotBeNull();
        capturedEstimate!.ChunksNeedingRehydration.ShouldBe(1);
        capturedEstimate.BytesNeedingRehydration.ShouldBe(tarChunkSize);

        await fixture.Mediator.Received(1).Publish(
            Arg.Is<RehydrationStartedEvent>(e => e.ChunkCount == 1 && e.TotalBytes == tarChunkSize),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AvailableTarTarget_ReportsFullTarChunkDownloadSize()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-tar-download-size-{Guid.NewGuid():N}",
            $"ctr-restore-tar-download-size-{Guid.NewGuid():N}");

        var targetPath = RelativePath.Parse("selected/tiny.bin");
        await fixture.LocalFileSystem.WriteAllBytesAsync(targetPath, [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("other/large-small-file.bin"), Enumerable.Repeat((byte)7, 128 * 1024).ToArray(), CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024,
                TarTargetSize      = 1024 * 1024,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        var tarChunk = await FindSingleChunkBlobAsync(blobs, BlobMetadataKeys.TypeTar);
        var tarMetadata = await blobs.GetMetadataAsync(tarChunk);
        var tarChunkSize = tarMetadata.ContentLength.ShouldNotBeNull();

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                TargetPath    = targetPath,
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        await fixture.Mediator.Received(1).Publish(
            Arg.Is<ChunkDownloadStartedEvent>(e => e.ChunkHash == ChunkHash.Parse(tarChunk.Name.ToString()) && e.ChunkSize == tarChunkSize),
            Arg.Any<CancellationToken>());
        await fixture.Mediator.Received(1).Publish(
            Arg.Is<ChunkDownloadCompletedEvent>(e => e.ChunkHash == ChunkHash.Parse(tarChunk.Name.ToString()) && e.ChunkSize == tarChunkSize),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DownloadFailure_FailsGracefullyWithoutDeadlock()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-download-fail-{Guid.NewGuid():N}",
            $"ctr-restore-download-fail-{Guid.NewGuid():N}");

        // Many 1:1 (large) chunks so the grouper is still emitting when a worker faults — exercises the
        // cancellation that unblocks the grouper from the bounded chunk channel.
        for (var i = 0; i < 12; i++)
            await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse($"dir{i}/file{i}.bin"), [(byte)i, (byte)(i + 1), (byte)(i + 2)], CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1, // every file is its own (large) chunk
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Delete one chunk blob so its download throws BlobNotFoundException mid-pipeline.
        var blobs = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        RelativePath? victim = null;
        await foreach (var item in ((IBlobContainerService)blobs).ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
        {
            victim = item.Name;
            break;
        }
        victim.ShouldNotBeNull();
        await blobs.DeleteAsync(victim.Value);

        var handleTask = fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None).AsTask();

        // A regression to deadlock would hang here; the timeout turns that into a clear failure.
        var result = await handleTask.WaitAsync(TimeSpan.FromSeconds(30));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Test]
    public async Task Handle_CancelRehydration_RestoresNothing()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-cancel-rehydration-{Guid.NewGuid():N}",
            $"ctr-restore-cancel-rehydration-{Guid.NewGuid():N}");

        var relativePath = RelativePath.Parse("archive/file.bin");
        var content      = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);

        // Archive to the Archive tier so the chunk classifies as needing rehydration.
        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Archive,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var confirmCalled = false;
        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
                // Cancel rehydration → nothing should be downloaded.
                ConfirmRehydration = (_, _) =>
                {
                    confirmCalled = true;
                    return Task.FromResult<RehydratePriority?>(null);
                },
            }),
            CancellationToken.None);

        confirmCalled.ShouldBeTrue("ConfirmRehydration should be invoked for an archive-tier chunk");
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(0);
        restoreResult.ChunksPendingRehydration.ShouldBeGreaterThanOrEqualTo(1);
        fixture.RestoreFileSystem.FileExists(relativePath).ShouldBeFalse();
    }

    [Test]
    public async Task Handle_ReadyRehydratedChunk_ReportsRehydratedStatus()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-rehydrated-status-{Guid.NewGuid():N}",
            $"ctr-restore-rehydrated-status-{Guid.NewGuid():N}");

        var relativePath = RelativePath.Parse("archive/rehydrated.bin");
        var content      = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Archive,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var contentHash = ContentHashOf(content, fixture.Encryption);
        var chunkHash   = ChunkHashOf(content, fixture.Encryption);
        var blobs       = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        var download    = await blobs.DownloadAsync(BlobPaths.ChunkPath(chunkHash));
        await using (download.Stream)
        await using (var rehydrated = new MemoryStream())
        {
            await download.Stream.CopyToAsync(rehydrated);
            blobs.SeedBlob(BlobPaths.ChunkRehydratedPath(chunkHash), rehydrated.ToArray(), BlobTier.Cool);
        }

        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        await fixture.Mediator.Received(1).Publish(
            Arg.Is<RehydrationStatusEvent>(e => e.Available == 0 && e.Rehydrated == 1 && e.NeedsRehydration == 0 && e.Pending == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PendingRehydratedChunk_DoesNotPromptOrPublishRehydrationStarted()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync(
            $"acct-restore-pending-rehydrated-{Guid.NewGuid():N}",
            $"ctr-restore-pending-rehydrated-{Guid.NewGuid():N}");

        var relativePath = RelativePath.Parse("archive/pending.bin");
        var content      = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Archive,
            }),
            CancellationToken.None);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var contentHash = ContentHashOf(content, fixture.Encryption);
        var chunkHash   = ChunkHashOf(content, fixture.Encryption);
        var blobs       = (FakeInMemoryBlobContainerService)fixture.BlobContainer;
        var download    = await blobs.DownloadAsync(BlobPaths.ChunkPath(chunkHash));
        await using (download.Stream)
        await using (var rehydrated = new MemoryStream())
        {
            await download.Stream.CopyToAsync(rehydrated);
            blobs.SeedBlob(BlobPaths.ChunkRehydratedPath(chunkHash), rehydrated.ToArray(), BlobTier.Archive);
        }

        var confirmCalled = false;
        var restoreResult = await fixture.CreateRestoreHandler().Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreDirectory.ToString(),
                Overwrite     = true,
                ConfirmRehydration = (_, _) =>
                {
                    confirmCalled = true;
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }),
            CancellationToken.None);

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.ChunksPendingRehydration.ShouldBe(1);
        confirmCalled.ShouldBeFalse("chunks already pending rehydration should not ask for a new rehydration priority");

        await fixture.Mediator.Received(1).Publish(
            Arg.Is<RehydrationStatusEvent>(e => e.Available == 0 && e.Rehydrated == 0 && e.NeedsRehydration == 0 && e.Pending == 1),
            Arg.Any<CancellationToken>());
        await fixture.Mediator.DidNotReceive().Publish(
            Arg.Any<RehydrationStartedEvent>(),
            Arg.Any<CancellationToken>());
    }

    private static async Task<byte[]> CompressAsync(byte[] plaintext)
    {
        using var output = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(plaintext);
        }

        return output.ToArray();
    }

    private static async Task AssertRestoreChunkIndexFailureLogAsync(
        ContentHash contentHash,
        Func<FakeSeededBlobContainerService, FileTreeHash, SnapshotManifest, IEncryptionService, Task> seedBlobs,
        string expectedCategory,
        string expectedGuidance,
        Func<LocalDirectory, CancellationToken, Task>? beforeHandle = null)
    {
        var blobs         = new FakeSeededBlobContainerService();
        var mediator      = Substitute.For<IMediator>();
        var logger        = new FakeLogger<RestoreCommandHandler>();
        var accountName   = $"acct-restore-index-failure-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-index-failure-{Guid.NewGuid():N}";
        var restoreRootDirectory = TestTempRoots.CreateDirectory("restore-index-failure");
        var restoreFileSystem = new RelativeFileSystem(restoreRootDirectory);

        restoreFileSystem.CreateDirectory(RelativePath.Root);
        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            var       snapshotSvc     = new SnapshotService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);
            using var index           = new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, snapshotSvc, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, accountName, containerName);

            var rootHash = FileTreeHashOf($"root-{contentHash.Short8}", IEncryptionService.PlaintextInstance);
            var snapshot = new SnapshotManifest
            {
                Timestamp    = DateTimeOffset.UtcNow,
                RootHash     = rootHash,
                FileCount    = 1,
                TotalSize    = 7,
                AriusVersion = "test"
            };

            await seedBlobs(blobs, rootHash, snapshot, IEncryptionService.PlaintextInstance);

            if (beforeHandle is not null)
                await beforeHandle(RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName), CancellationToken.None);

            var handler = new RestoreCommandHandler(IEncryptionService.PlaintextInstance, index, new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), fileTreeService, snapshotSvc, mediator, logger, accountName, containerName);

            var result = await handler.Handle(new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions { RootDirectory = restoreRootDirectory.ToString(), Overwrite = true }), CancellationToken.None);

            result.Success.ShouldBeFalse();

            var messages = logger.Collector
                .GetSnapshot(clear: false)
                .Select(static record => record.Message)
                .ToArray();

            messages.ShouldContain(message =>
                message.StartsWith($"[chunk] Chunk-index resolution failed: category={expectedCategory};", StringComparison.Ordinal) &&
                message.Contains(expectedGuidance, StringComparison.Ordinal));
            messages.ShouldContain(static message => message.StartsWith("[restore] Failure:", StringComparison.Ordinal));
        }
        finally
        {
            RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

            restoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    private static async Task<RelativePath> FindSingleChunkBlobAsync(IBlobContainerService blobs, string ariusType)
    {
        RelativePath? match = null;
        await foreach (var item in blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
        {
            var metadata = await blobs.GetMetadataAsync(item.Name);
            if (!metadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var type) || type != ariusType)
                continue;

            match.ShouldBeNull($"expected exactly one {ariusType} chunk blob");
            match = item.Name;
        }

        return match.ShouldNotBeNull($"expected one {ariusType} chunk blob");
    }

}
