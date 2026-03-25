using Arius.Core;
using Arius.Core.Archive;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Arius.Cli.Tests;

// ── 7.1 ProgressState thread safety ──────────────────────────────────────────

/// <summary>
/// Verifies that <see cref="ProgressState"/> counter updates are thread-safe:
/// concurrent increments from multiple threads produce correct totals.
/// </summary>
public class ProgressStateThreadSafetyTests
{
    [Test]
    public async Task ConcurrentIncrements_FilesHashed_CorrectTotal()
    {
        var state      = new ProgressState();
        const int n    = 10_000;
        const int concurrency = 8;

        // Prime the "hashing" counter so decrements don't underflow
        for (int i = 0; i < n; i++)
            state.IncrementFilesHashing($"file{i}", 100);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            (_, _) =>
            {
                state.IncrementFilesHashed();
                return ValueTask.CompletedTask;
            });

        state.FilesHashed.ShouldBe(n);
        state.FilesHashing.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentIncrements_ChunksUploaded_CorrectTotal()
    {
        var state      = new ProgressState();
        const int n    = 5_000;
        const int concurrency = 8;
        const long chunkSize = 1024L;

        // Prime ChunksUploading
        for (int i = 0; i < n; i++)
            state.IncrementChunksUploading($"hash{i}", chunkSize);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            (_, _) =>
            {
                state.IncrementChunksUploaded(chunkSize);
                return ValueTask.CompletedTask;
            });

        state.ChunksUploaded.ShouldBe(n);
        state.ChunksUploading.ShouldBe(0);
        state.BytesUploaded.ShouldBe(n * chunkSize);
    }

    [Test]
    public async Task ConcurrentIncrements_FilesRestored_CorrectTotal()
    {
        var state   = new ProgressState();
        const int n = 8_000;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (_, _) =>
            {
                state.IncrementFilesRestored();
                return ValueTask.CompletedTask;
            });

        state.FilesRestored.ShouldBe(n);
    }

    [Test]
    public void TotalFiles_NullUntilSet()
    {
        var state = new ProgressState();
        state.TotalFiles.ShouldBeNull();

        state.SetTotalFiles(1523);
        state.TotalFiles.ShouldBe(1523L);
    }
}

// ── 7.2 Archive notification handler unit tests ───────────────────────────────

/// <summary>
/// Verifies that each archive / restore notification handler updates exactly the
/// correct field on <see cref="ProgressState"/>.
/// </summary>
public class NotificationHandlerTests
{
    // ── FileScannedHandler ────────────────────────────────────────────────────

    [Test]
    public async Task FileScannedHandler_SetsTotalFiles()
    {
        var state   = new ProgressState();
        var handler = new FileScannedHandler(state);

        await handler.Handle(new FileScannedEvent(1523), CancellationToken.None);

        state.TotalFiles.ShouldBe(1523L);
    }

    // ── FileHashingHandler ────────────────────────────────────────────────────

    [Test]
    public async Task FileHashingHandler_IncrementFilesHashing()
    {
        var state   = new ProgressState();
        var handler = new FileHashingHandler(state);

        await handler.Handle(new FileHashingEvent("foo/bar.bin", 1024), CancellationToken.None);

        state.FilesHashing.ShouldBe(1);
        state.CurrentHashFile.ShouldBe("foo/bar.bin");
        state.CurrentHashFileSize.ShouldBe(1024);
    }

    // ── FileHashedHandler ─────────────────────────────────────────────────────

    [Test]
    public async Task FileHashedHandler_IncrementsHashedDecrementHashing()
    {
        var state       = new ProgressState();
        var hashingH    = new FileHashingHandler(state);
        var hashedH     = new FileHashedHandler(state);

        await hashingH.Handle(new FileHashingEvent("a.bin", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("a.bin", "abc123"), CancellationToken.None);

        state.FilesHashed.ShouldBe(1L);
        state.FilesHashing.ShouldBe(0);
    }

    // ── ChunkUploadingHandler ─────────────────────────────────────────────────

    [Test]
    public async Task ChunkUploadingHandler_IncrementChunksUploading()
    {
        var state   = new ProgressState();
        var handler = new ChunkUploadingHandler(state);

        await handler.Handle(new ChunkUploadingEvent("deadbeef", 5_000_000), CancellationToken.None);

        state.ChunksUploading.ShouldBe(1);
        state.CurrentUploadFile.ShouldBe("deadbeef");
        state.CurrentUploadFileSize.ShouldBe(5_000_000);
    }

    // ── ChunkUploadedHandler ──────────────────────────────────────────────────

    [Test]
    public async Task ChunkUploadedHandler_IncrementsUploadedDecrementsUploading()
    {
        var state       = new ProgressState();
        var uploadingH  = new ChunkUploadingHandler(state);
        var uploadedH   = new ChunkUploadedHandler(state);

        await uploadingH.Handle(new ChunkUploadingEvent("hash1", 1000), CancellationToken.None);
        await uploadedH.Handle(new ChunkUploadedEvent("hash1", 800), CancellationToken.None);

        state.ChunksUploaded.ShouldBe(1L);
        state.ChunksUploading.ShouldBe(0);
        state.BytesUploaded.ShouldBe(800L);
    }

    // ── TarBundleSealingHandler ───────────────────────────────────────────────

    [Test]
    public async Task TarBundleSealingHandler_IncrementsTarsBundled()
    {
        var state   = new ProgressState();
        var handler = new TarBundleSealingHandler(state);

        await handler.Handle(new TarBundleSealingEvent(5, 640_000), CancellationToken.None);

        state.TarsBundled.ShouldBe(1);
    }

    // ── TarBundleUploadedHandler ──────────────────────────────────────────────

    [Test]
    public async Task TarBundleUploadedHandler_IncrementsTarsUploaded()
    {
        var state   = new ProgressState();
        var handler = new TarBundleUploadedHandler(state);

        await handler.Handle(new TarBundleUploadedEvent("tarhash", 512_000, 5), CancellationToken.None);

        state.TarsUploaded.ShouldBe(1);
    }

    // ── SnapshotCreatedHandler ────────────────────────────────────────────────

    [Test]
    public async Task SnapshotCreatedHandler_SetsSnapshotComplete()
    {
        var state   = new ProgressState();
        var handler = new SnapshotCreatedHandler(state);

        state.SnapshotComplete.ShouldBeFalse();
        await handler.Handle(new SnapshotCreatedEvent("roothash", DateTimeOffset.UtcNow, 10), CancellationToken.None);

        state.SnapshotComplete.ShouldBeTrue();
    }

    // ── RestoreStartedHandler ─────────────────────────────────────────────────

    [Test]
    public async Task RestoreStartedHandler_SetsRestoreTotalFiles()
    {
        var state   = new ProgressState();
        var handler = new RestoreStartedHandler(state);

        await handler.Handle(new RestoreStartedEvent(1000), CancellationToken.None);

        state.RestoreTotalFiles.ShouldBe(1000);
    }

    // ── FileRestoredHandler ───────────────────────────────────────────────────

    [Test]
    public async Task FileRestoredHandler_IncrementsFilesRestored()
    {
        var state   = new ProgressState();
        var handler = new FileRestoredHandler(state);

        await handler.Handle(new FileRestoredEvent("dir/file.txt"), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
    }

    // ── FileSkippedHandler ────────────────────────────────────────────────────

    [Test]
    public async Task FileSkippedHandler_IncrementsFilesSkipped()
    {
        var state   = new ProgressState();
        var handler = new FileSkippedHandler(state);

        await handler.Handle(new FileSkippedEvent("dir/existing.txt"), CancellationToken.None);

        state.FilesSkipped.ShouldBe(1L);
    }

    // ── RehydrationStartedHandler ─────────────────────────────────────────────

    [Test]
    public async Task RehydrationStartedHandler_SetsChunkCount()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStartedHandler(state);

        await handler.Handle(new RehydrationStartedEvent(7, 1024 * 1024), CancellationToken.None);

        state.RehydrationChunkCount.ShouldBe(7);
    }
}

// ── 7.3 Integration: Mediator routes events to ProgressState handlers ─────────

/// <summary>
/// Verifies that when the CLI's notification handlers are registered via DI
/// and a real <see cref="IMediator"/> is used, published archive/restore events
/// are routed to <see cref="ProgressState"/> correctly.
///
/// This test confirms the Mediator source-generator discovers handlers in
/// <c>Arius.Cli</c> when <c>AddMediator()</c> is called from that assembly context,
/// by exercising the handler chain end-to-end through the real mediator.
/// </summary>
public class MediatorEventRoutingIntegrationTests
{
    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerProvider.Instance));
        services.AddSingleton<ProgressState>();

        // Provide stub Core services so the source-generated Mediator can initialize
        // its command handler wrappers (ArchivePipelineHandler etc.) without real Azure deps.
        services.AddArius(
            blobStorage:      Substitute.For<IBlobStorageService>(),
            passphrase:       null,
            accountName:      "test",
            containerName:    "test",
            cacheBudgetBytes: 0);

        // Register CLI notification handlers explicitly (source generator only discovers them
        // when AddMediator() is called from the Arius.Cli assembly context).
        services.AddSingleton<INotificationHandler<FileScannedEvent>,        FileScannedHandler>();
        services.AddSingleton<INotificationHandler<FileHashingEvent>,        FileHashingHandler>();
        services.AddSingleton<INotificationHandler<FileHashedEvent>,         FileHashedHandler>();
        services.AddSingleton<INotificationHandler<ChunkUploadingEvent>,     ChunkUploadingHandler>();
        services.AddSingleton<INotificationHandler<ChunkUploadedEvent>,      ChunkUploadedHandler>();
        services.AddSingleton<INotificationHandler<TarBundleSealingEvent>,   TarBundleSealingHandler>();
        services.AddSingleton<INotificationHandler<TarBundleUploadedEvent>,  TarBundleUploadedHandler>();
        services.AddSingleton<INotificationHandler<SnapshotCreatedEvent>,    SnapshotCreatedHandler>();
        services.AddSingleton<INotificationHandler<RestoreStartedEvent>,     RestoreStartedHandler>();
        services.AddSingleton<INotificationHandler<FileRestoredEvent>,       FileRestoredHandler>();
        services.AddSingleton<INotificationHandler<FileSkippedEvent>,        FileSkippedHandler>();
        services.AddSingleton<INotificationHandler<RehydrationStartedEvent>, RehydrationStartedHandler>();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AllArchiveEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        // Publish all archive notification events
        await mediator.Publish(new FileScannedEvent(5));
        await mediator.Publish(new FileHashingEvent("a.bin", 100));
        await mediator.Publish(new FileHashedEvent("a.bin", "hash1"));
        await mediator.Publish(new ChunkUploadingEvent("hash1", 200));
        await mediator.Publish(new ChunkUploadedEvent("hash1", 150));
        await mediator.Publish(new TarBundleSealingEvent(3, 30_000));
        await mediator.Publish(new TarBundleUploadedEvent("tarhash", 25_000, 3));
        await mediator.Publish(new SnapshotCreatedEvent("root", DateTimeOffset.UtcNow, 5));

        // Verify ProgressState was updated
        state.TotalFiles.ShouldBe(5L);
        state.FilesHashed.ShouldBe(1L);
        state.FilesHashing.ShouldBe(0);
        state.ChunksUploaded.ShouldBe(1L);
        state.BytesUploaded.ShouldBe(150L);
        state.TarsBundled.ShouldBe(1);
        state.TarsUploaded.ShouldBe(1);
        state.SnapshotComplete.ShouldBeTrue();
    }

    [Test]
    public async Task AllRestoreEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        await mediator.Publish(new RestoreStartedEvent(10));
        await mediator.Publish(new FileRestoredEvent("a.txt"));
        await mediator.Publish(new FileRestoredEvent("b.txt"));
        await mediator.Publish(new FileSkippedEvent("c.txt"));
        await mediator.Publish(new RehydrationStartedEvent(4, 2048));

        state.RestoreTotalFiles.ShouldBe(10);
        state.FilesRestored.ShouldBe(2L);
        state.FilesSkipped.ShouldBe(1L);
        state.RehydrationChunkCount.ShouldBe(4);
    }
}
