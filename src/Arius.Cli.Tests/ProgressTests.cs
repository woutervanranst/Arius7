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
            (i, _) =>
            {
                state.IncrementFilesHashed($"file{i}");
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
            (i, _) =>
            {
                state.IncrementChunksUploaded($"hash{i}", chunkSize);
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
        state.InFlightHashes.ContainsKey("foo/bar.bin").ShouldBeTrue();
        state.InFlightHashes["foo/bar.bin"].TotalBytes.ShouldBe(1024L);
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
        state.InFlightUploads.ContainsKey("deadbeef").ShouldBeTrue();
        state.InFlightUploads["deadbeef"].TotalBytes.ShouldBe(5_000_000L);
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

// ── 7.2b FileProgress Interlocked update test ────────────────────────────────

/// <summary>
/// Verifies that <see cref="FileProgress.SetBytesProcessed"/> correctly reports
/// the latest value under concurrent updates.
/// </summary>
public class FileProgressTests
{
    [Test]
    public async Task BytesProcessed_UpdatesCorrectlyUnderContention()
    {
        var fp = new FileProgress("test.bin", 1_000_000L);

        // Simulate concurrent progress reports from a single stream reader.
        // Each "tick" reports cumulative bytes, so last-writer-wins is the correct behaviour.
        const int iterations = 10_000;
        long      finalValue = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(1, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                fp.SetBytesProcessed(i);
                Interlocked.Exchange(ref finalValue, i);
                return ValueTask.CompletedTask;
            });

        // After all updates BytesProcessed must be a valid value in [1..iterations]
        fp.BytesProcessed.ShouldBeInRange(1L, iterations);
    }
}

// ── 7.2c Restore TCS coordination tests ──────────────────────────────────────

/// <summary>
/// Verifies the TCS phase-coordination pattern used in the restore command:
/// the question TCS signals the CLI, the CLI unblocks the answer TCS, and the
/// pipeline completes without deadlock.
/// </summary>
public class RestoreTcsCoordinationTests
{
    [Test]
    public async Task ConfirmRehydration_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        // Arrange: simulate the ConfirmRehydration callback wiring
        var questionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // "Pipeline" fires the question and waits for the answer
        var pipelineTask = Task.Run(async () =>
        {
            questionTcs.TrySetResult(42);         // signal: question ready
            return await answerTcs.Task;          // block: waiting for answer
        });

        // "CLI event loop" waits for either pipeline completion or a question
        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);

        firstSignal.ShouldBe(questionTcs.Task, "question should arrive before pipeline completes");

        var questionValue = await questionTcs.Task;
        questionValue.ShouldBe(42);

        // CLI provides the answer — unblocks the pipeline
        answerTcs.TrySetResult(true);

        var answer = await pipelineTask;
        answer.ShouldBeTrue("pipeline should receive the answer we provided");
    }

    [Test]
    public async Task ConfirmCleanup_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // "Pipeline" cleanup callback
        var pipelineTask = Task.Run(async () =>
        {
            cleanupQuestionTcs.TrySetResult((3, 1024L));
            return await cleanupAnswerTcs.Task;
        });

        // Await the question
        var (count, bytes) = await cleanupQuestionTcs.Task;
        count.ShouldBe(3);
        bytes.ShouldBe(1024L);

        // Provide answer
        cleanupAnswerTcs.TrySetResult(false);

        var result = await pipelineTask;
        result.ShouldBeFalse();
    }

    [Test]
    public async Task NoRehydrationNeeded_PipelineCompletesFirst_QuestionTcsNeverSet()
    {
        var questionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pipeline completes immediately without setting the question TCS
        var pipelineTask = Task.FromResult(true);

        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);
        firstSignal.ShouldBe((Task)pipelineTask, "pipeline should complete first");

        questionTcs.Task.IsCompleted.ShouldBeFalse("question TCS should not be set");
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

        // AddMediator() must be called in the outermost (test) assembly so the source
        // generator discovers INotificationHandler<T> implementations in both Core and CLI.
        services.AddMediator();

        // Provide stub Core services so the source-generated Mediator can initialize
        // its command handler wrappers (ArchivePipelineHandler etc.) without real Azure deps.
        services.AddArius(
            blobStorage:      Substitute.For<IBlobStorageService>(),
            passphrase:       null,
            accountName:      "test",
            containerName:    "test",
            cacheBudgetBytes: 0);

        // No manual handler registration needed: the source generator in this test assembly
        // discovers all INotificationHandler<T> implementations in both Arius.Core and Arius.Cli.
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
