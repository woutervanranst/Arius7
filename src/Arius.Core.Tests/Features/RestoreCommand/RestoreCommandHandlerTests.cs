using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreCommandHandlerTests
{
    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var       blobs           = new ThrowOnCreateBlobContainerService("restore");
        var       encryption      = new PlaintextPassthroughService();
        using var index           = new ChunkIndexService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var       fileTreeService = new FileTreeService(blobs, encryption, index, "acct-restore-missing", "ctr-restore-missing");
        var       snapshotSvc     = new SnapshotService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var       mediator        = Substitute.For<IMediator>();
        var       logger          = new FakeLogger<RestoreCommandHandler>();

        var handler = new RestoreCommandHandler(
            encryption,
            index,
            new ChunkStorageService(blobs, encryption),
            fileTreeService,
            snapshotSvc,
            mediator,
            logger,
            "acct-restore-missing",
            "ctr-restore-missing");

        var result = await handler.Handle(
            new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions { RootDirectory = Path.GetTempPath() }),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No snapshots found in this repository.");
        blobs.CreateCalled.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_Restores_DuplicateLargeChunkContent_ToAllPaths()
    {
        var blobs         = new FakeInMemoryBlobContainerService();
        var encryption    = new PlaintextPassthroughService();
        var mediator      = Substitute.For<IMediator>();
        var accountName   = $"acct-restore-duplicates-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-duplicates-{Guid.NewGuid():N}";
        var localRoot     = Path.Combine(Path.GetTempPath(), $"arius-restore-local-{Guid.NewGuid():N}");
        var restoreRoot   = Path.Combine(Path.GetTempPath(), $"arius-restore-output-{Guid.NewGuid():N}");

        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);
        Directory.CreateDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName));
        Directory.CreateDirectory(FileTreeService.GetDiskCacheDirectory(accountName, containerName));

        try
        {
            var content = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(content);

            WriteFile("archives/duplicates/binary-a.bin", content);
            WriteFile("nested/deep/a/b/c/binary-b.bin", content);

            using var index           = new ChunkIndexService(blobs, encryption, accountName, containerName);
            var       chunkStorage    = new ChunkStorageService(blobs, encryption);
            var       fileTreeService = new FileTreeService(blobs, encryption, index, accountName, containerName);
            var       snapshotSvc     = new SnapshotService(blobs, encryption, accountName, containerName);

            var archiveHandler = new ArchiveCommandHandler(blobs, encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, new FakeLogger<ArchiveCommandHandler>(), accountName, containerName);

            var archiveResult = await archiveHandler.Handle(
                new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = localRoot,
                    UploadTier = BlobTier.Cool,
                }),
                CancellationToken.None);

            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            var restoreHandler = new RestoreCommandHandler(encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, new FakeLogger<RestoreCommandHandler>(), accountName, containerName);

            var restoreResult = await restoreHandler.Handle(
                new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
                {
                    RootDirectory = restoreRoot,
                    Overwrite = true,
                }),
                CancellationToken.None);

            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(2);
            File.ReadAllBytes(Path.Combine(restoreRoot, "archives/duplicates/binary-a.bin")).ShouldBe(content);
            File.ReadAllBytes(Path.Combine(restoreRoot, "nested/deep/a/b/c/binary-b.bin")).ShouldBe(content);
        }
        finally
        {
            if (Directory.Exists(localRoot))
                Directory.Delete(localRoot, recursive: true);

            if (Directory.Exists(restoreRoot))
                Directory.Delete(restoreRoot, recursive: true);

            TryDeleteDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName));
            TryDeleteDirectory(FileTreeService.GetDiskCacheDirectory(accountName, containerName));
            TryDeleteDirectory(SnapshotService.GetDiskCacheDirectory(accountName, containerName));
        }

        void WriteFile(string relativePath, byte[] bytes)
        {
            var fullPath = Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }

        static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (DirectoryNotFoundException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    [Test]
    public async Task Handle_Restores_DuplicateTarEntryContent_ToAllPaths_WithPerPathMetadata()
    {
        var blobs         = new FakeInMemoryBlobContainerService();
        var encryption    = new PlaintextPassthroughService();
        var mediator      = Substitute.For<IMediator>();
        var accountName   = $"acct-restore-tar-duplicates-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-tar-duplicates-{Guid.NewGuid():N}";
        var localRoot     = Path.Combine(Path.GetTempPath(), $"arius-restore-local-{Guid.NewGuid():N}");
        var restoreRoot   = Path.Combine(Path.GetTempPath(), $"arius-restore-output-{Guid.NewGuid():N}");

        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);
        Directory.CreateDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName));
        Directory.CreateDirectory(FileTreeService.GetDiskCacheDirectory(accountName, containerName));

        try
        {
            var content = new byte[512 * 1024];
            Random.Shared.NextBytes(content);

            var firstCreated   = new DateTime(2021, 4, 5, 6, 7,  8,  DateTimeKind.Utc);
            var firstModified  = new DateTime(2022, 5, 6, 7, 8,  9,  DateTimeKind.Utc);
            var secondCreated  = new DateTime(2023, 6, 7, 8, 9,  10, DateTimeKind.Utc);
            var secondModified = new DateTime(2024, 7, 8, 9, 10, 11, DateTimeKind.Utc);

            WriteFile("archives/duplicates/copy-a.bin", content, firstCreated, firstModified);
            WriteFile("nested/deep/a/b/c/copy-b.bin", content, secondCreated, secondModified);

            using var index           = new ChunkIndexService(blobs, encryption, accountName, containerName);
            var       chunkStorage    = new ChunkStorageService(blobs, encryption);
            var       fileTreeService = new FileTreeService(blobs, encryption, index, accountName, containerName);
            var       snapshotSvc     = new SnapshotService(blobs, encryption, accountName, containerName);

            var archiveHandler = new ArchiveCommandHandler(blobs, encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, new FakeLogger<ArchiveCommandHandler>(), accountName, containerName);

            var archiveResult = await archiveHandler.Handle(
                new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = localRoot,
                    UploadTier = BlobTier.Cool,
                    SmallFileThreshold = 1024 * 1024,
                }),
                CancellationToken.None);

            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            var restoreHandler = new RestoreCommandHandler(encryption, index, chunkStorage, fileTreeService, snapshotSvc, mediator, new FakeLogger<RestoreCommandHandler>(), accountName, containerName);
            var restoreResult = await restoreHandler.Handle(
                new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
                {
                    RootDirectory = restoreRoot,
                    Overwrite = true,
                }),
                CancellationToken.None);

            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(2);

            AssertRestoredFile("archives/duplicates/copy-a.bin", content, firstCreated, firstModified);
            AssertRestoredFile("nested/deep/a/b/c/copy-b.bin", content, secondCreated, secondModified);
        }
        finally
        {
            if (Directory.Exists(localRoot))
                Directory.Delete(localRoot, recursive: true);

            if (Directory.Exists(restoreRoot))
                Directory.Delete(restoreRoot, recursive: true);

            TryDeleteDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName));
            TryDeleteDirectory(FileTreeService.GetDiskCacheDirectory(accountName, containerName));
            TryDeleteDirectory(SnapshotService.GetDiskCacheDirectory(accountName, containerName));
        }

        void WriteFile(string relativePath, byte[] bytes, DateTime created, DateTime modified)
        {
            var fullPath = Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            File.SetCreationTimeUtc(fullPath, created);
            File.SetLastWriteTimeUtc(fullPath, modified);
        }

        void AssertRestoredFile(string relativePath, byte[] expectedContent, DateTime expectedCreated, DateTime expectedModified)
        {
            var restoredPath = Path.Combine(restoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var pointerPath = restoredPath + ".pointer.arius";

            File.ReadAllBytes(restoredPath).ShouldBe(expectedContent);
            File.Exists(pointerPath).ShouldBeTrue($"Pointer file should exist for {relativePath}");

            if (!OperatingSystem.IsLinux())
            {
                File.GetCreationTimeUtc(restoredPath).ShouldBe(expectedCreated, $"Binary CreationTimeUtc for {relativePath}");
                File.GetCreationTimeUtc(pointerPath).ShouldBe(expectedCreated, $"Pointer CreationTimeUtc for {relativePath}");
            }

            File.GetLastWriteTimeUtc(restoredPath).ShouldBe(expectedModified, $"Binary LastWriteTimeUtc for {relativePath}");
            File.GetLastWriteTimeUtc(pointerPath).ShouldBe(expectedModified, $"Pointer LastWriteTimeUtc for {relativePath}");
        }

        static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (DirectoryNotFoundException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    [Test]
    public async Task Handle_InvalidSnapshotContentHash_FailsRestore()
    {
        var blobs = new FakeSeededBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        var mediator = Substitute.For<IMediator>();
        var accountName = $"acct-restore-invalid-{Guid.NewGuid():N}";
        var containerName = $"ctr-restore-invalid-{Guid.NewGuid():N}";
        var restoreRoot = Path.Combine(Path.GetTempPath(), $"arius-restore-invalid-{Guid.NewGuid():N}");

        Directory.CreateDirectory(restoreRoot);

        try
        {
            using var index = new ChunkIndexService(blobs, encryption, accountName, containerName);
            var fileTreeService = new FileTreeService(blobs, encryption, index, accountName, containerName);
            var snapshotSvc = new SnapshotService(blobs, encryption, accountName, containerName);

            var rootHash = FileTreeHash.Parse(encryption.ComputeHash("root-broken"u8.ToArray()).ToString());
            var snapshot = new SnapshotManifest
            {
                Timestamp    = DateTimeOffset.UtcNow,
                RootHash     = rootHash,
                FileCount    = 2,
                TotalSize    = 2,
                AriusVersion = "test"
            };

            var validHash = ContentHash.Parse(encryption.ComputeHash("healthy"u8.ToArray()).ToString());
            var chunkHash = ChunkHash.Parse(validHash);
            index.AddEntry(new ShardEntry(validHash, chunkHash, OriginalSize: 7, CompressedSize: 7));

            var invalidTreePayload = System.Text.Encoding.UTF8.GetBytes($"not-a-hash F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} broken.txt\n{validHash} F {DateTimeOffset.UtcNow:O} {DateTimeOffset.UtcNow:O} healthy.txt\n");
            blobs.AddBlob(BlobPaths.FileTree(rootHash),                 await CompressAsync(invalidTreePayload));
            blobs.AddBlob(BlobPaths.Chunk(chunkHash),                   await CompressAsync("healthy"u8.ToArray()));
            blobs.AddBlob(SnapshotService.BlobName(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, encryption));

            var handler = new RestoreCommandHandler(encryption, index, new ChunkStorageService(blobs, encryption), fileTreeService, snapshotSvc, mediator, new FakeLogger<RestoreCommandHandler>(), accountName, containerName);

            var result = await handler.Handle(new Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions { RootDirectory = restoreRoot, Overwrite = true }), CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FilesRestored.ShouldBe(0);
            result.FilesSkipped.ShouldBe(0);
            result.ErrorMessage.ShouldNotBeNull();
            result.ErrorMessage.ShouldContain("not-a-hash");
            File.Exists(Path.Combine(restoreRoot, "broken.txt")).ShouldBeFalse();
            File.Exists(Path.Combine(restoreRoot, "healthy.txt")).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(restoreRoot))
                Directory.Delete(restoreRoot, recursive: true);
        }
    }

    private static async Task<byte[]> CompressAsync(byte[] plaintext)
    {
        using var output = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(plaintext);
        }

        return output.ToArray();
    }

}
