using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Features.ArchiveCommand.Fakes;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using TUnit.Core;
using ArchiveCommandType = Arius.Core.Features.ArchiveCommand.ArchiveCommand;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class ArchiveFinalizationTests
{
    [Test]
    public async Task Archive_Finalization_OverlapsFlushWithTreeWork_AndWaitsForFlushBeforeSnapshot()
    {
        const string account = "acct-finalization";
        var container = $"ctr-finalization-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), $"arius-finalization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "large.bin"), new byte[2 * 1024 * 1024]);
            await File.WriteAllBytesAsync(Path.Combine(root, "small.txt"), new byte[256]);

            var blobs = new CoordinatedArchiveBlobContainerService();
            var encryption = new PlaintextPassthroughService();
            var index = new ChunkIndexService(blobs, encryption, account, container);
            var fileTreeService = new FileTreeService(blobs, encryption, index, account, container);
            var snapshotService = new SnapshotService(blobs, encryption, account, container);
            var chunkStorage = new ChunkStorageService(blobs, encryption);
            var mediator = Substitute.For<IMediator>();
            var logger = new FakeLogger<ArchiveCommandHandler>();

            var handler = new ArchiveCommandHandler(
                blobs,
                encryption,
                index,
                chunkStorage,
                fileTreeService,
                snapshotService,
                mediator,
                logger,
                account,
                container);

            var archiveTask = handler.Handle(
                new ArchiveCommandType(new ArchiveCommandOptions
                {
                    RootDirectory = root,
                    UploadTier = BlobTier.Hot,
                }),
                CancellationToken.None).AsTask();

            await blobs.TreeUploadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            blobs.IndexUploadCompleted.IsCompleted.ShouldBeFalse("tree upload should start before index flush completes");

            blobs.AllowTreeUpload();
            blobs.AllowIndexUpload();

            var result = await archiveTask;

            result.Success.ShouldBeTrue(result.ErrorMessage);
            blobs.SnapshotUploadedBeforeIndexCompleted.ShouldBeFalse("snapshot must wait for chunk-index flush completion");
            blobs.IndexUploadCompleted.IsCompleted.ShouldBeTrue();

            await mediator.Received().Publish(
                Arg.Is<ChunkIndexFlushProgressEvent>(e => e.ShardsCompleted >= 1 && e.TotalShards >= 1),
                Arg.Any<CancellationToken>());

            await mediator.Received().Publish(
                Arg.Is<TreeUploadProgressEvent>(e => e.BlobsUploaded >= 1 && e.TotalBlobs >= 1),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            var repoDir = RepositoryPaths.GetRepositoryDirectory(account, container);
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public async Task Archive_PartialFinalizationBeforeSnapshot_DoesNotCreateCompletedArchiveState()
    {
        const string account = "acct-commit-point";
        var container = $"ctr-commit-point-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), $"arius-commit-point-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "large.bin"), new byte[2 * 1024 * 1024]);
            await File.WriteAllBytesAsync(Path.Combine(root, "small.txt"), new byte[256]);

            var blobs = new CoordinatedArchiveBlobContainerService();
            var encryption = new PlaintextPassthroughService();
            var index = new ChunkIndexService(blobs, encryption, account, container);
            var fileTreeService = new FileTreeService(blobs, encryption, index, account, container);
            var snapshotService = new SnapshotService(blobs, encryption, account, container);
            var chunkStorage = new ChunkStorageService(blobs, encryption);
            var mediator = Substitute.For<IMediator>();
            var logger = new FakeLogger<ArchiveCommandHandler>();

            var handler = new ArchiveCommandHandler(
                blobs,
                encryption,
                index,
                chunkStorage,
                fileTreeService,
                snapshotService,
                mediator,
                logger,
                account,
                container);

            var archiveTask = handler.Handle(
                new ArchiveCommandType(new ArchiveCommandOptions
                {
                    RootDirectory = root,
                    UploadTier = BlobTier.Hot,
                }),
                CancellationToken.None).AsTask();

            await blobs.TreeUploadStarted.WaitAsync(TimeSpan.FromSeconds(5));

            blobs.HasAnyBlobWithPrefix(BlobPaths.Snapshots).ShouldBeFalse();

            blobs.AllowTreeUpload();
            await blobs.TreeUploadCompleted.WaitAsync(TimeSpan.FromSeconds(5));

            blobs.HasAnyBlobWithPrefix(BlobPaths.FileTrees).ShouldBeTrue();
            blobs.HasAnyBlobWithPrefix(BlobPaths.Snapshots).ShouldBeFalse();

            var visibleSnapshotBeforeCommit = await snapshotService.ResolveAsync();
            visibleSnapshotBeforeCommit.ShouldBeNull();

            blobs.AllowIndexUpload();

            var result = await archiveTask;

            result.Success.ShouldBeTrue(result.ErrorMessage);

            var visibleSnapshotAfterCommit = await snapshotService.ResolveAsync();
            visibleSnapshotAfterCommit.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            var repoDir = RepositoryPaths.GetRepositoryDirectory(account, container);
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }
}
