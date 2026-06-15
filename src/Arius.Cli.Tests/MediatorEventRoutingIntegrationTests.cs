using Arius.Core;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies that when the CLI's notification handlers are registered via DI
/// and a real <see cref="IMediator"/> is used, published archive/restore events
/// are routed to <see cref="ProgressState"/> correctly.
/// </summary>
public class MediatorEventRoutingIntegrationTests
{
    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerProvider.Instance));
        services.AddSingleton<ProgressState>();

        // AddMediator() must be called in the outermost (test) assembly so the source
        // generator discovers INotificationHandler<T> implementations in both Core and CLI.
        services.AddMediator();

        // Provide stub Core services so the source-generated Mediator can initialize.
        services.AddArius(
            blobContainer: Substitute.For<IBlobContainerService>(),
            passphrase: null,
            accountName: "test",
            containerName: "test");

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AllArchiveEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        // Publish all archive notification events in pipeline order
        await mediator.Publish(new FileScannedEvent(RelativePath.Parse("a.bin"), 100));
        await mediator.Publish(new ScanCompleteEvent(1, 100));
        await mediator.Publish(new FileHashingEvent(RelativePath.Parse("a.bin"), 100));
        await mediator.Publish(new FileHashedEvent(RelativePath.Parse("a.bin"), FakeContentHash('a')));

        // A file that becomes unreadable mid-pipeline is tracked, then skipped -> row removed.
        // (Archive FileSkippedEvent must be fully qualified: RestoreCommand also defines one.)
        await mediator.Publish(new FileHashingEvent(RelativePath.Parse("skipped.bin"), 50));
        await mediator.Publish(new Arius.Core.Features.ArchiveCommand.FileSkippedEvent(RelativePath.Parse("skipped.bin")));

        // Small file -> TAR bundle path
        await mediator.Publish(new TarBundleStartedEvent());
        await mediator.Publish(new TarEntryAddedEvent(FakeContentHash('a'), 1, 100));
        await mediator.Publish(new TarBundleSealingEvent(1, 100, 100, FakeChunkHash('b'), [FakeContentHash('a')]));
        await mediator.Publish(new ChunkUploadingEvent(FakeChunkHash('b'), 100));
        await mediator.Publish(new TarBundleUploadedEvent(FakeChunkHash('b'), 80, 1));

        // Large file -> single-chunk upload path: chunk hash bridges back to the content hash.
        await mediator.Publish(new FileHashingEvent(RelativePath.Parse("large.bin"), 200));
        await mediator.Publish(new FileHashedEvent(RelativePath.Parse("large.bin"), FakeContentHash('d')));
        await mediator.Publish(new ChunkUploadingEvent(FakeChunkHash('d'), 200));
        await mediator.Publish(new ChunkUploadedEvent(FakeChunkHash('d'), 150));

        await mediator.Publish(new SnapshotCreatedEvent(FakeFileTreeHash('c'), DateTimeOffset.UtcNow, 1));

        // Verify ProgressState was updated
        state.FilesScanned.ShouldBe(1L);
        state.TotalFiles.ShouldBe(1L);
        state.ScanComplete.ShouldBeTrue();
        state.FilesHashed.ShouldBe(2L);          // a.bin + large.bin
        // The skipped file is counted toward hashing completion so the headline can reach "done"
        // (hashed + skipped >= scanned) instead of stalling one short of the scanned total.
        state.FilesSkippedHashing.ShouldBe(1L);  // skipped.bin
        state.TarsUploaded.ShouldBe(1L);
        state.ChunksUploaded.ShouldBe(2L);       // large-file chunk + tar bundle
        state.BytesUploaded.ShouldBe(230L);      // 150 (large file) + 80 (tar bundle)
        // Every tracked-file row is cleared by its terminal event:
        //   a.bin       -> TarEntryAddedEvent
        //   skipped.bin -> FileSkippedEvent
        //   large.bin   -> ChunkUploadedEvent
        state.TrackedFiles.ContainsKey(RelativePath.Parse("a.bin")).ShouldBeFalse();
        state.TrackedFiles.ContainsKey(RelativePath.Parse("skipped.bin")).ShouldBeFalse();
        state.TrackedFiles.ContainsKey(RelativePath.Parse("large.bin")).ShouldBeFalse();
        state.SnapshotComplete.ShouldBeTrue();
    }

    [Test]
    public async Task AllRestoreEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        var snapshotTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Snapshot resolved: records the snapshot timestamp + root hash.
        await mediator.Publish(new SnapshotResolvedEvent(snapshotTime, FakeFileTreeHash('e')));

        // Tree traversal: progress ticks up as entries are discovered, then completes with the final count + total size.
        await mediator.Publish(new TreeTraversalProgressEvent(7));
        await mediator.Publish(new TreeTraversalCompleteEvent(10, 3500));

        // Per-file route decisions: a.txt/b.txt are new, c.txt already matches on disk (-> later skipped).
        await mediator.Publish(new FileRoutedEvent(RelativePath.Parse("a.txt"), RestoreRoute.New, 1000L));
        await mediator.Publish(new FileRoutedEvent(RelativePath.Parse("b.txt"), RestoreRoute.New, 2000L));
        await mediator.Publish(new FileRoutedEvent(RelativePath.Parse("c.txt"), RestoreRoute.SkipIdentical, 500L));

        // Chunk index lookups done: total/large/tar chunk counts + compressed byte total.
        await mediator.Publish(new ChunkResolutionCompleteEvent(3, 1, 1, 1800));

        // Rehydration: availability check, then the request is kicked off for 4 chunks (2048 bytes total).
        await mediator.Publish(new RehydrationStatusEvent(1, 0, 2, 1));
        await mediator.Publish(new RehydrationStartedEvent(4, 2048));

        // Tar-bundle chunk download: "started" stashes display metadata; "completed" adds to bytes-downloaded.
        await mediator.Publish(new ChunkDownloadStartedEvent(FakeChunkHash('f'), "tar", 2, 900, 2500));
        await mediator.Publish(new ChunkDownloadCompletedEvent(FakeChunkHash('f'), 2, 900));

        // Two files written to disk: each bumps FilesRestored/BytesRestored and appends a recent-events row.
        await mediator.Publish(new FileRestoredEvent(RelativePath.Parse("a.txt"), 1000L));
        await mediator.Publish(new FileRestoredEvent(RelativePath.Parse("b.txt"), 2000L));

        // A file already present with a matching hash is skipped. Unlike the archive FileSkippedEvent (path only),
        // the restore one carries a size and bumps FilesSkipped/BytesSkipped. Fully qualified: ArchiveCommand also defines one.
        await mediator.Publish(new Arius.Core.Features.RestoreCommand.FileSkippedEvent(RelativePath.Parse("c.txt"), 500L));

        // Cleanup of rehydrated blobs finished. Handler is intentionally a no-op (reserved), so there is nothing to assert.
        await mediator.Publish(new CleanupCompleteEvent(3, 1800));

        // Verify ProgressState was updated (grouped by the event that set each value)

        // TreeTraversalCompleteEvent
        state.RestoreTotalFiles.ShouldBe(10);

        // SnapshotResolvedEvent
        state.SnapshotTimestamp.ShouldBe(snapshotTime);
        state.SnapshotRootHash.ShouldBe(FakeFileTreeHash('e'));

        // TreeTraversalProgressEvent + TreeTraversalCompleteEvent
        state.RestoreFilesDiscovered.ShouldBe(7L);
        state.TreeTraversalComplete.ShouldBeTrue();
        state.RestoreTotalOriginalSize.ShouldBe(3500L);

        // FileRoutedEvent
        state.RouteNew.ShouldBe(2);
        state.RouteSkipIdentical.ShouldBe(1);

        // ChunkResolutionCompleteEvent
        state.RestoreTotalChunks.ShouldBe(3);
        state.LargeChunkCount.ShouldBe(1);
        state.TarChunkCount.ShouldBe(1);
        state.RestoreTotalChunkBytes.ShouldBe(1800L);

        // RehydrationStatusEvent
        state.ChunksAvailable.ShouldBe(1);
        state.ChunksRehydrated.ShouldBe(0);
        state.ChunksNeedingRehydration.ShouldBe(2);
        state.ChunksPending.ShouldBe(1);

        // RehydrationStartedEvent
        state.RehydrationChunkCount.ShouldBe(4);
        state.RehydrationTotalBytes.ShouldBe(2048L);

        // ChunkDownloadStartedEvent (tar metadata) + ChunkDownloadCompletedEvent (bytes downloaded)
        state.TarBundleMetadata[FakeChunkHash('f')].ShouldBe((2, 2500L));
        state.RestoreBytesDownloaded.ShouldBe(900L);

        // FileRestoredEvent (a.txt + b.txt) + FileSkippedEvent (c.txt)
        state.FilesRestored.ShouldBe(2L);
        state.BytesRestored.ShouldBe(3000L);
        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(500L);

        // FileRestoredEvent + FileSkippedEvent also append to the rolling recent-events window, in publish order.
        state.RecentRestoreEvents.Select(e => e.RelativePath).ShouldBe([
            RelativePath.Parse("a.txt"),
            RelativePath.Parse("b.txt"),
            RelativePath.Parse("c.txt")
        ]);
    }
}
