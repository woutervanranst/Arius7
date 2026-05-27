using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
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
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var accountName   = $"acct-restore-missing-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-missing-{Guid.NewGuid():N}";

        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            var       blobs           = new ThrowOnCreateBlobContainerService("restore");
            var       encryption      = new PlaintextPassthroughService();
            using var index           = new ChunkIndexService(blobs, encryption, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, encryption, index, accountName, containerName);
            var       snapshotSvc     = new SnapshotService(blobs, encryption, accountName, containerName);
            var       mediator        = Substitute.For<IMediator>();
            var       logger          = new FakeLogger<RestoreCommandHandler>();

            var handler = new RestoreCommandHandler(encryption, index, new ChunkStorageService(blobs, encryption), fileTreeService, snapshotSvc, mediator, logger, accountName, containerName);

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
        var encryption    = new PlaintextPassthroughService();
        var mediator      = Substitute.For<IMediator>();
        var accountName   = $"acct-restore-invalid-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-invalid-{Guid.NewGuid():N}";
        var restoreRootDirectory = TestTempRoots.CreateDirectory("restore-invalid");
        var restoreFileSystem = new RelativeFileSystem(restoreRootDirectory);

        restoreFileSystem.CreateDirectory(RelativePath.Root);
        RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);

        try
        {
            using var index           = new ChunkIndexService(blobs, encryption, accountName, containerName);
            var       fileTreeService = new FileTreeService(blobs, encryption, index, accountName, containerName);
            var       snapshotSvc     = new SnapshotService(blobs, encryption, accountName, containerName);

            var rootHash = FileTreeHash.Parse(encryption.ComputeHash("root-broken"u8).ToString());
            var snapshot = new SnapshotManifest
            {
                Timestamp    = DateTimeOffset.UtcNow,
                RootHash     = rootHash,
                FileCount    = 2,
                TotalSize    = 2,
                AriusVersion = "test"
            };

            var validHash = ContentHash.Parse(encryption.ComputeHash("healthy"u8).ToString());
            var chunkHash = ChunkHash.Parse(validHash);
            index.AddEntry(new ShardEntry(validHash, chunkHash, OriginalSize: 7, CompressedSize: 7));

            var invalidTreePayload = System.Text.Encoding.UTF8.GetBytes($"not-a-hash F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} broken.txt\n{validHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} healthy.txt\n");
            blobs.AddBlob(BlobPaths.FileTreePath(rootHash),             await CompressAsync(invalidTreePayload));
            blobs.AddBlob(BlobPaths.ChunkPath(chunkHash),               await CompressAsync("healthy"u8.ToArray()));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, encryption));

            var handler = new RestoreCommandHandler(encryption, index, new ChunkStorageService(blobs, encryption), fileTreeService, snapshotSvc, mediator, new FakeLogger<RestoreCommandHandler>(), accountName, containerName);

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

    private static async Task<byte[]> CompressAsync(byte[] plaintext)
    {
        using var output = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(plaintext);
        }

        return output.ToArray();
    }

}
