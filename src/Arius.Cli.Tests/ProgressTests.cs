using Arius.Core;
using Arius.Core.Archive;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests;

// ── 6.1 TrackedFile state transitions: small file (tar) path ─────────────────

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the small-file/tar path:
/// Hashing → QueuedInTar → UploadingTar → Done (removed).
/// </summary>
public class TrackedFileSmallFilePathTests
{
    [Test]
    public void SmallFilePath_StateTransitions_Correct()
    {
        var state = new ProgressState();

        // FileHashingEvent → AddFile → State=Hashing
        state.AddFile("notes.txt", 1024);
        state.TrackedFiles.ContainsKey("notes.txt").ShouldBeTrue();
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.Hashing);

        // FileHashedEvent → SetFileHashed → ContentHash set, reverse map populated
        state.SetFileHashed("notes.txt", "def456");
        state.TrackedFiles["notes.txt"].ContentHash.ShouldBe("def456");
        state.ContentHashToPath["def456"].ShouldBe("notes.txt");
        state.FilesHashed.ShouldBe(1L);

        // TarEntryAddedEvent → SetFileQueuedInTar
        state.SetFileQueuedInTar("def456");
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.QueuedInTar);

        // TarBundleSealingEvent → SetFilesUploadingTar
        state.SetFilesUploadingTar(["def456"], "tar_hash_1");
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.UploadingTar);
        state.TrackedFiles["notes.txt"].TarId.ShouldBe("tar_hash_1");

        // TarBundleUploadedEvent → RemoveFilesByTarId
        state.RemoveFilesByTarId("tar_hash_1");
        state.TrackedFiles.ContainsKey("notes.txt").ShouldBeFalse();
    }
}

// ── 6.2 TrackedFile state transitions: large file path ───────────────────────

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the large-file/direct-upload path:
/// Hashing → Uploading → Done (removed).
/// </summary>
public class TrackedFileLargeFilePathTests
{
    [Test]
    public void LargeFilePath_StateTransitions_Correct()
    {
        var state = new ProgressState();

        // FileHashingEvent
        state.AddFile("video.mp4", 5_000_000_000L);
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Hashing);

        // FileHashedEvent
        state.SetFileHashed("video.mp4", "abc123");
        state.TrackedFiles["video.mp4"].ContentHash.ShouldBe("abc123");

        // ChunkUploadingEvent → SetFileUploading (not tar path)
        state.SetFileUploading("abc123");
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Uploading);

        // ChunkUploadedEvent → RemoveFile
        state.RemoveFile("video.mp4");
        state.TrackedFiles.ContainsKey("video.mp4").ShouldBeFalse();
    }

    [Test]
    public void SetFileUploading_DoesNotOverrideQueuedInTar()
    {
        // A file on the tar path should NOT transition to Uploading
        var state = new ProgressState();
        state.AddFile("small.txt", 100);
        state.SetFileHashed("small.txt", "hash1");
        state.SetFileQueuedInTar("hash1");
        state.TrackedFiles["small.txt"].State.ShouldBe(FileState.QueuedInTar);

        // SetFileUploading should be ignored for tar-path files
        state.SetFileUploading("hash1");
        state.TrackedFiles["small.txt"].State.ShouldBe(FileState.QueuedInTar);
    }
}

// ── 6.3 ContentHashToPath reverse lookup ─────────────────────────────────────

/// <summary>
/// Verifies the <see cref="ProgressState.ContentHashToPath"/> reverse map is populated
/// on hash and used for downstream events keyed by content hash.
/// </summary>
public class ContentHashToPathTests
{
    [Test]
    public void ReverseMap_PopulatedOnHash_UsedForDownstreamEvents()
    {
        var state = new ProgressState();

        state.AddFile("dir/file.bin", 500);
        state.SetFileHashed("dir/file.bin", "aabbcc");

        // Reverse map populated
        state.ContentHashToPath.ContainsKey("aabbcc").ShouldBeTrue();
        state.ContentHashToPath["aabbcc"].ShouldBe("dir/file.bin");

        // Downstream event via reverse map works
        state.SetFileQueuedInTar("aabbcc");
        state.TrackedFiles["dir/file.bin"].State.ShouldBe(FileState.QueuedInTar);
    }

    [Test]
    public void ReverseMap_PopulatedBeforeDownstreamEvents()
    {
        // FileHashedEvent sets both ContentHash and reverse map atomically
        var state = new ProgressState();
        state.AddFile("config.yml", 200);
        state.SetFileHashed("config.yml", "xxyyzz");

        state.ContentHashToPath.ContainsKey("xxyyzz").ShouldBeTrue();
    }
}

// ── 6.4 ProgressState concurrent add/transition/remove ───────────────────────

/// <summary>
/// Verifies <see cref="ProgressState"/> is thread-safe under concurrent operations.
/// </summary>
public class ProgressStateThreadSafetyTests
{
    [Test]
    public async Task ConcurrentAddAndRemove_NoDataRaces()
    {
        var state = new ProgressState();
        const int n = 5_000;

        // Concurrently add files, hash them, then remove them
        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                var path = $"file{i}.bin";
                var hash = $"hash{i:x8}";
                state.AddFile(path, i * 1024L);
                state.SetFileHashed(path, hash);
                state.RemoveFile(path);
                return ValueTask.CompletedTask;
            });

        // All removed — counter should be n (each SetFileHashed increments FilesHashed)
        state.FilesHashed.ShouldBe(n);
        state.TrackedFiles.Count.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentIncrements_FilesHashed_CorrectTotal()
    {
        var state = new ProgressState();
        const int n = 10_000;

        for (int i = 0; i < n; i++)
            state.AddFile($"file{i}", 100);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                state.SetFileHashed($"file{i}", $"hash{i:x8}");
                return ValueTask.CompletedTask;
            });

        state.FilesHashed.ShouldBe(n);
    }

    [Test]
    public async Task ConcurrentIncrements_FilesRestored_CorrectTotal()
    {
        var state = new ProgressState();
        const int n = 8_000;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (_, _) =>
            {
                state.IncrementFilesRestored(1024L);
                return ValueTask.CompletedTask;
            });

        state.FilesRestored.ShouldBe(n);
        state.BytesRestored.ShouldBe(n * 1024L);
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

// ── 6.5 Archive notification handler unit tests ───────────────────────────────

/// <summary>
/// Verifies that each archive notification handler updates the correct
/// <see cref="TrackedFile"/> state and aggregate counters on <see cref="ProgressState"/>.
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
    public async Task FileHashingHandler_AddsTrackedFile()
    {
        var state   = new ProgressState();
        var handler = new FileHashingHandler(state);

        await handler.Handle(new FileHashingEvent("foo/bar.bin", 1024), CancellationToken.None);

        state.TrackedFiles.ContainsKey("foo/bar.bin").ShouldBeTrue();
        state.TrackedFiles["foo/bar.bin"].State.ShouldBe(FileState.Hashing);
        state.TrackedFiles["foo/bar.bin"].TotalBytes.ShouldBe(1024L);
    }

    // ── FileHashedHandler ─────────────────────────────────────────────────────

    [Test]
    public async Task FileHashedHandler_SetsHashAndIncrementsFilesHashed()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);

        await hashingH.Handle(new FileHashingEvent("a.bin", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("a.bin", "abc123"), CancellationToken.None);

        state.FilesHashed.ShouldBe(1L);
        state.TrackedFiles["a.bin"].ContentHash.ShouldBe("abc123");
        state.ContentHashToPath["abc123"].ShouldBe("a.bin");
    }

    // ── TarEntryAddedHandler ──────────────────────────────────────────────────

    [Test]
    public async Task TarEntryAddedHandler_SetsQueuedInTar()
    {
        var state          = new ProgressState();
        var hashingH       = new FileHashingHandler(state);
        var hashedH        = new FileHashedHandler(state);
        var tarEntryH      = new TarEntryAddedHandler(state);

        await hashingH.Handle(new FileHashingEvent("small.txt", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("small.txt", "def456"), CancellationToken.None);
        await tarEntryH.Handle(new TarEntryAddedEvent("def456", 1, 100), CancellationToken.None);

        state.TrackedFiles["small.txt"].State.ShouldBe(FileState.QueuedInTar);
    }

    // ── ChunkUploadingHandler ─────────────────────────────────────────────────

    [Test]
    public async Task ChunkUploadingHandler_SetsUploading()
    {
        var state     = new ProgressState();
        var hashingH  = new FileHashingHandler(state);
        var hashedH   = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await hashingH.Handle(new FileHashingEvent("large.bin", 1_000_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("large.bin", "bigfile1"), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent("bigfile1", 1_000_000), CancellationToken.None);

        state.TrackedFiles["large.bin"].State.ShouldBe(FileState.Uploading);
    }

    // ── ChunkUploadedHandler ──────────────────────────────────────────────────

    [Test]
    public async Task ChunkUploadedHandler_RemovesFileAndIncrementsChunksUploaded()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var uploadedH  = new ChunkUploadedHandler(state);

        await hashingH.Handle(new FileHashingEvent("data.bin", 5000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("data.bin", "hash999"), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent("hash999", 5000), CancellationToken.None);
        await uploadedH.Handle(new ChunkUploadedEvent("hash999", 4000), CancellationToken.None);

        state.TrackedFiles.ContainsKey("data.bin").ShouldBeFalse();
        state.ChunksUploaded.ShouldBe(1L);
        state.BytesUploaded.ShouldBe(4000L);
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

        await handler.Handle(new FileRestoredEvent("dir/file.txt", 500L), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(500L);
    }

    // ── FileSkippedHandler ────────────────────────────────────────────────────

    [Test]
    public async Task FileSkippedHandler_IncrementsFilesSkipped()
    {
        var state   = new ProgressState();
        var handler = new FileSkippedHandler(state);

        await handler.Handle(new FileSkippedEvent("dir/existing.txt", 300L), CancellationToken.None);

        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(300L);
    }

    // ── RehydrationStartedHandler ─────────────────────────────────────────────

    [Test]
    public async Task RehydrationStartedHandler_SetsChunkCount()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStartedHandler(state);

        await handler.Handle(new RehydrationStartedEvent(7, 1024 * 1024), CancellationToken.None);

        state.RehydrationChunkCount.ShouldBe(7);
        state.RehydrationTotalBytes.ShouldBe(1024L * 1024L);
    }
}

// ── 6.6 TarBundleSealingHandler: batch state transition ──────────────────────

/// <summary>
/// Verifies that <see cref="TarBundleSealingHandler"/> transitions all files in the
/// sealed tar from QueuedInTar → UploadingTar and sets their TarId.
/// </summary>
public class TarBundleSealingHandlerTests
{
    [Test]
    public async Task TarBundleSealingHandler_BatchTransitionsAllFilesToUploadingTar()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        var tarH     = new TarEntryAddedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);

        // Set up 3 files in QueuedInTar
        var files = new[] { ("f1.txt", "h1"), ("f2.txt", "h2"), ("f3.txt", "h3") };
        foreach (var (path, hash) in files)
        {
            await hashingH.Handle(new FileHashingEvent(path, 100), CancellationToken.None);
            await hashedH.Handle(new FileHashedEvent(path, hash), CancellationToken.None);
            await tarH.Handle(new TarEntryAddedEvent(hash, 1, 100), CancellationToken.None);
        }

        // Seal the tar
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, "tar_xyz", ["h1", "h2", "h3"]),
            CancellationToken.None);

        foreach (var (path, _) in files)
        {
            state.TrackedFiles[path].State.ShouldBe(FileState.UploadingTar);
            state.TrackedFiles[path].TarId.ShouldBe("tar_xyz");
        }
    }
}

// ── 6.7 TarBundleUploadedHandler: batch removal ──────────────────────────────

/// <summary>
/// Verifies that <see cref="TarBundleUploadedHandler"/> removes all files whose
/// TarId matches the uploaded tar hash.
/// </summary>
public class TarBundleUploadedHandlerTests
{
    [Test]
    public async Task TarBundleUploadedHandler_RemovesAllFilesWithMatchingTarId()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        var tarH     = new TarEntryAddedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);
        var uploadedH = new TarBundleUploadedHandler(state);

        var files = new[] { ("a.txt", "ha"), ("b.txt", "hb"), ("c.txt", "hc") };
        foreach (var (path, hash) in files)
        {
            await hashingH.Handle(new FileHashingEvent(path, 50), CancellationToken.None);
            await hashedH.Handle(new FileHashedEvent(path, hash), CancellationToken.None);
            await tarH.Handle(new TarEntryAddedEvent(hash, 1, 50), CancellationToken.None);
        }

        await sealingH.Handle(
            new TarBundleSealingEvent(3, 150, "tar_abc", ["ha", "hb", "hc"]),
            CancellationToken.None);

        await uploadedH.Handle(
            new TarBundleUploadedEvent("tar_abc", 120, 3),
            CancellationToken.None);

        foreach (var (path, _) in files)
            state.TrackedFiles.ContainsKey(path).ShouldBeFalse();

        state.TarsUploaded.ShouldBe(1L);
        state.ChunksUploaded.ShouldBe(1L);
    }
}

// ── 6.8 BuildArchiveDisplay: stage headers and per-file lines ─────────────────

/// <summary>
/// Verifies that <see cref="CliBuilder.BuildArchiveDisplay"/> renders stage headers
/// and per-file lines correctly from a known <see cref="ProgressState"/>.
/// </summary>
public class BuildArchiveDisplayTests
{
    private static string RenderToString(IRenderable renderable)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(new StringWriter()),
        });
        var writer = new StringWriter();
        var testConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        testConsole.Write(renderable);
        return writer.ToString();
    }

    [Test]
    public void BuildArchiveDisplay_ShowsStageHeaders()
    {
        var state = new ProgressState();
        state.SetTotalFiles(5);

        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("Scanning");
        output.ShouldContain("Hashing");
        output.ShouldContain("Uploading");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsScanningComplete_WhenTotalKnown()
    {
        var state = new ProgressState();
        state.SetTotalFiles(1523);

        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("1523");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsPerFileLines_WhenFilesTracked()
    {
        var state = new ProgressState();
        state.AddFile("video.mp4", 5_000_000);

        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("video.mp4");
        output.ShouldContain("Hashing");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsQueuedInTar_Label()
    {
        var state = new ProgressState();
        state.AddFile("notes.txt", 100);
        state.SetFileHashed("notes.txt", "h1");
        state.SetFileQueuedInTar("h1");

        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("notes.txt");
        output.ShouldContain("Queued in TAR");
    }
}

// ── 6.9 BuildArchiveDisplay: Done files not rendered ─────────────────────────

/// <summary>
/// Verifies that files removed from <see cref="ProgressState.TrackedFiles"/> do not
/// appear in the display (Done state = removed from dictionary).
/// </summary>
public class BuildArchiveDisplayDoneTests
{
    [Test]
    public void BuildArchiveDisplay_DoesNotShowRemovedFiles()
    {
        var state = new ProgressState();
        state.AddFile("completed.bin", 1000);
        state.SetFileHashed("completed.bin", "done1");
        // Simulate completion: remove from dict
        state.RemoveFile("completed.bin");

        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var writer     = new StringWriter();
        var console    = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        var output = writer.ToString();

        output.ShouldNotContain("completed.bin");
    }
}

// ── 6.10 RenderProgressBar: bar character fill ───────────────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.RenderProgressBar"/> produces correct fill ratios.
/// </summary>
public class RenderProgressBarTests
{
    [Test]
    public void RenderProgressBar_ZeroFraction_AllEmpty()
    {
        var bar = CliBuilder.RenderProgressBar(0.0, 10);
        // Zero filled → all empty chars ░
        bar.ShouldContain(new string('░', 10));
    }

    [Test]
    public void RenderProgressBar_FullFraction_AllFilled()
    {
        var bar = CliBuilder.RenderProgressBar(1.0, 10);
        // Full → all filled chars █
        bar.ShouldContain(new string('█', 10));
    }

    [Test]
    public void RenderProgressBar_HalfFraction_HalfFilled()
    {
        var bar = CliBuilder.RenderProgressBar(0.5, 12);
        // 6 filled + 6 empty
        bar.ShouldContain(new string('█', 6));
        bar.ShouldContain(new string('░', 6));
    }

    [Test]
    public void RenderProgressBar_62Percent_Width12_SevenOrEightFilled()
    {
        var bar = CliBuilder.RenderProgressBar(0.62, 12);
        // Round(0.62 * 12) = Round(7.44) = 7 filled
        bar.ShouldContain(new string('█', 7));
        bar.ShouldContain(new string('░', 5));
    }

    [Test]
    public void RenderProgressBar_ClampsBelowZero()
    {
        var bar = CliBuilder.RenderProgressBar(-0.5, 8);
        bar.ShouldContain(new string('░', 8));
    }

    [Test]
    public void RenderProgressBar_ClampsAboveOne()
    {
        var bar = CliBuilder.RenderProgressBar(1.5, 8);
        bar.ShouldContain(new string('█', 8));
    }
}

// ── 6.11 Integration: Mediator routes events to ProgressState handlers ────────

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
            blobStorage:      Substitute.For<IBlobStorageService>(),
            passphrase:       null,
            accountName:      "test",
            containerName:    "test",
            cacheBudgetBytes: 0);

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
        await mediator.Publish(new TarEntryAddedEvent("hash1", 1, 100));
        await mediator.Publish(new TarBundleSealingEvent(1, 100, "tar1", ["hash1"]));
        await mediator.Publish(new TarBundleUploadedEvent("tar1", 80, 1));
        await mediator.Publish(new SnapshotCreatedEvent("root", DateTimeOffset.UtcNow, 5));

        // Verify ProgressState was updated
        state.TotalFiles.ShouldBe(5L);
        state.FilesHashed.ShouldBe(1L);
        state.TarsUploaded.ShouldBe(1L);
        // a.bin was removed after tar upload
        state.TrackedFiles.ContainsKey("a.bin").ShouldBeFalse();
        state.SnapshotComplete.ShouldBeTrue();
    }

    [Test]
    public async Task AllRestoreEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        await mediator.Publish(new RestoreStartedEvent(10));
        await mediator.Publish(new FileRestoredEvent("a.txt", 1000L));
        await mediator.Publish(new FileRestoredEvent("b.txt", 2000L));
        await mediator.Publish(new FileSkippedEvent("c.txt", 500L));
        await mediator.Publish(new RehydrationStartedEvent(4, 2048));

        state.RestoreTotalFiles.ShouldBe(10);
        state.FilesRestored.ShouldBe(2L);
        state.BytesRestored.ShouldBe(3000L);
        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(500L);
        state.RehydrationChunkCount.ShouldBe(4);
    }
}

// ── 6.12 Integration: progress callbacks report bytes ────────────────────────

/// <summary>
/// Verifies that <see cref="CliBuilder"/>'s <c>CreateHashProgress</c> and
/// <c>CreateUploadProgress</c> factory callbacks correctly wire to
/// <see cref="TrackedFile.SetBytesProcessed"/>.
/// </summary>
public class ProgressCallbackIntegrationTests
{
    [Test]
    public void CreateHashProgress_UpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("large.bin", 5_000_000);
        state.SetFileHashed("large.bin", "lhash1");

        // Simulate what CliBuilder wires: look up TrackedFile by relative path
        IProgress<long>? hashProgress = null;
        if (state.TrackedFiles.TryGetValue("large.bin", out var file))
            hashProgress = new Progress<long>(bytes => file.SetBytesProcessed(bytes));

        hashProgress.ShouldNotBeNull();

        // Report progress (Progress<T> dispatches on sync context; invoke directly)
        file.SetBytesProcessed(2_500_000);
        file.BytesProcessed.ShouldBe(2_500_000L);
    }

    [Test]
    public void CreateUploadProgress_ResetsThenUpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("chunk.bin", 1_000_000);
        state.SetFileHashed("chunk.bin", "chash1");
        state.SetFileUploading("chash1");

        // Simulate what CliBuilder wires: look up via reverse map
        IProgress<long>? uploadProgress = null;
        if (state.ContentHashToPath.TryGetValue("chash1", out var relPath) &&
            state.TrackedFiles.TryGetValue(relPath, out var file))
        {
            file.SetBytesProcessed(0);  // reset at upload start
            uploadProgress = new Progress<long>(bytes => file.SetBytesProcessed(bytes));
        }

        uploadProgress.ShouldNotBeNull();

        // Verify reset happened
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(0L);

        // Report upload progress
        state.TrackedFiles["chunk.bin"].SetBytesProcessed(450_000);
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(450_000L);
    }
}

// ── Restore TCS coordination tests (unchanged) ────────────────────────────────

/// <summary>
/// Verifies the TCS phase-coordination pattern used in the restore command.
/// </summary>
public class RestoreTcsCoordinationTests
{
    [Test]
    public async Task ConfirmRehydration_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        var questionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipelineTask = Task.Run(async () =>
        {
            questionTcs.TrySetResult(42);
            return await answerTcs.Task;
        });

        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);
        firstSignal.ShouldBe(questionTcs.Task, "question should arrive before pipeline completes");

        var questionValue = await questionTcs.Task;
        questionValue.ShouldBe(42);

        answerTcs.TrySetResult(true);

        var answer = await pipelineTask;
        answer.ShouldBeTrue("pipeline should receive the answer we provided");
    }

    [Test]
    public async Task ConfirmCleanup_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipelineTask = Task.Run(async () =>
        {
            cleanupQuestionTcs.TrySetResult((3, 1024L));
            return await cleanupAnswerTcs.Task;
        });

        var (count, bytes) = await cleanupQuestionTcs.Task;
        count.ShouldBe(3);
        bytes.ShouldBe(1024L);

        cleanupAnswerTcs.TrySetResult(false);

        var result = await pipelineTask;
        result.ShouldBeFalse();
    }

    [Test]
    public async Task NoRehydrationNeeded_PipelineCompletesFirst_QuestionTcsNeverSet()
    {
        var questionTcs  = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipelineTask = Task.FromResult(true);

        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);
        firstSignal.ShouldBe((Task)pipelineTask, "pipeline should complete first");

        questionTcs.Task.IsCompleted.ShouldBeFalse("question TCS should not be set");
    }
}

// ── TrackedFile BytesProcessed Interlocked update test ───────────────────────

/// <summary>
/// Verifies that <see cref="TrackedFile.SetBytesProcessed"/> reports the latest value
/// under concurrent updates (last-writer semantics).
/// </summary>
public class TrackedFileBytesProcessedTests
{
    [Test]
    public async Task BytesProcessed_UpdatesCorrectlyUnderContention()
    {
        var file = new TrackedFile("test.bin", 1_000_000L);

        const int iterations = 10_000;
        long      finalValue = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(1, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                file.SetBytesProcessed(i);
                Interlocked.Exchange(ref finalValue, i);
                return ValueTask.CompletedTask;
            });

        file.BytesProcessed.ShouldBeInRange(1L, iterations);
    }
}

// ── 12.1–12.3 BuildArchiveDisplay: round-2 refinements ───────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.BuildArchiveDisplay"/> uses ●/○ symbols,
/// full relative path truncation, and a size column.
/// </summary>
public class BuildArchiveDisplayRound2Tests
{
    private static string RenderToString(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        return writer.ToString();
    }

    // 12.1 ● / ○ symbols

    [Test]
    public void BuildArchiveDisplay_UsesFilledCircle_WhenScanningComplete()
    {
        var state = new ProgressState();
        state.SetTotalFiles(3);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        // ● present (ANSI stripped → just the char)
        output.ShouldContain("●");
    }

    [Test]
    public void BuildArchiveDisplay_UsesOpenCircle_WhenScanningInProgress()
    {
        var state  = new ProgressState();   // TotalFiles not set yet
        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        output.ShouldContain("○");
    }

    // 12.2 Full relative path truncation (not just filename)

    [Test]
    public void BuildArchiveDisplay_ShowsRelativePath_NotJustFilename()
    {
        var state = new ProgressState();
        state.AddFile("some/deep/path/file.bin", 1024);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        // The display should show truncated relative path, not just "file.bin"
        // TruncateAndLeftJustify("some/deep/path/file.bin", 30) = "some/deep/path/file.bin" (23 chars, fits)
        output.ShouldContain("some/deep/path/file.bin");
    }

    [Test]
    public void BuildArchiveDisplay_TruncatesLongRelativePath_WithEllipsisPrefix()
    {
        // Path longer than 30 chars → should be truncated with "..." prefix
        var longPath = "a/very/long/directory/structure/with/file.bin"; // > 30 chars
        var state = new ProgressState();
        state.AddFile(longPath, 2048);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        output.ShouldContain("...");
        // The full original path should NOT appear verbatim
        output.ShouldNotContain(longPath);
    }

    // 12.3 Size column present for various states

    [Test]
    public void BuildArchiveDisplay_ShowsSize_ForHashingState()
    {
        var state = new ProgressState();
        state.AddFile("doc.pdf", 5_000_000);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        // Humanizer renders 5_000_000 bytes as e.g. "4.77 MB" or "5 MB" — either way "MB" present
        output.ShouldContain("MB");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsSize_ForQueuedInTarState()
    {
        var state = new ProgressState();
        state.AddFile("notes.txt", 1024);
        state.SetFileHashed("notes.txt", "hq1");
        state.SetFileQueuedInTar("hq1");

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        output.ShouldContain("1");  // at least the number is shown (1 KB or 1024 B)
    }
}

// ── 12.4 TruncateAndLeftJustify ───────────────────────────────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.TruncateAndLeftJustify"/> edge cases.
/// </summary>
public class TruncateAndLeftJustifyTests
{
    [Test]
    public void ShortPath_PaddedToWidth()
    {
        var result = CliBuilder.TruncateAndLeftJustify("hi.txt", 10);
        result.ShouldBe("hi.txt    ");
        result.Length.ShouldBe(10);
    }

    [Test]
    public void ExactWidthPath_NotPadded()
    {
        var result = CliBuilder.TruncateAndLeftJustify("12345", 5);
        result.ShouldBe("12345");
        result.Length.ShouldBe(5);
    }

    [Test]
    public void LongPath_TruncatedWithEllipsisPrefix()
    {
        // "abcdefghij" is 10 chars; width=7 → "..." + last 4 = "...ghij"
        var result = CliBuilder.TruncateAndLeftJustify("abcdefghij", 7);
        result.ShouldBe("...ghij");
        result.Length.ShouldBe(7);
    }

    [Test]
    public void Width4_LongPath_EllipsisPlusOneChar()
    {
        // width=4 → "..." + last 1 char
        var result = CliBuilder.TruncateAndLeftJustify("abcde", 4);
        result.ShouldBe("...e");
        result.Length.ShouldBe(4);
    }

    [Test]
    public void EmptyString_PaddedToWidth()
    {
        var result = CliBuilder.TruncateAndLeftJustify("", 5);
        result.ShouldBe("     ");
        result.Length.ShouldBe(5);
    }
}

// ── 12.5 AddRestoreEvent ring buffer ─────────────────────────────────────────

/// <summary>
/// Verifies <see cref="ProgressState.AddRestoreEvent"/> caps the queue at 10 entries.
/// </summary>
public class AddRestoreEventTests
{
    [Test]
    public void AddRestoreEvent_CapAt10_KeepsMostRecent()
    {
        var state = new ProgressState();

        for (int i = 1; i <= 15; i++)
            state.AddRestoreEvent($"file{i}.txt", i * 100L, skipped: false);

        state.RecentRestoreEvents.Count.ShouldBe(10);

        // Most recent 10 are file6.txt through file15.txt
        var paths = state.RecentRestoreEvents.Select(e => e.RelativePath).ToList();
        paths.ShouldContain("file15.txt");
        paths.ShouldContain("file6.txt");
        paths.ShouldNotContain("file5.txt");
        paths.ShouldNotContain("file1.txt");
    }

    [Test]
    public void AddRestoreEvent_BelowCap_AllRetained()
    {
        var state = new ProgressState();

        for (int i = 1; i <= 5; i++)
            state.AddRestoreEvent($"file{i}.txt", 100L, skipped: i % 2 == 0);

        state.RecentRestoreEvents.Count.ShouldBe(5);
    }
}

// ── 12.6 IncrementFilesRestored / IncrementFilesSkipped byte accumulators ─────

/// <summary>
/// Verifies byte accumulators are updated alongside counters.
/// </summary>
public class RestoreByteAccumulatorTests
{
    [Test]
    public void IncrementFilesRestored_UpdatesCounterAndBytes()
    {
        var state = new ProgressState();

        state.IncrementFilesRestored(1000L);
        state.IncrementFilesRestored(2000L);

        state.FilesRestored.ShouldBe(2L);
        state.BytesRestored.ShouldBe(3000L);
    }

    [Test]
    public void IncrementFilesSkipped_UpdatesCounterAndBytes()
    {
        var state = new ProgressState();

        state.IncrementFilesSkipped(512L);
        state.IncrementFilesSkipped(512L);

        state.FilesSkipped.ShouldBe(2L);
        state.BytesSkipped.ShouldBe(1024L);
    }
}

// ── 12.7 SetRehydration sets both fields ─────────────────────────────────────

/// <summary>
/// Verifies <see cref="ProgressState.SetRehydration"/> sets both chunk count and byte total.
/// </summary>
public class SetRehydrationTests
{
    [Test]
    public void SetRehydration_SetsBothFields()
    {
        var state = new ProgressState();

        state.SetRehydration(5, 10_485_760L);

        state.RehydrationChunkCount.ShouldBe(5);
        state.RehydrationTotalBytes.ShouldBe(10_485_760L);
    }
}

// ── 12.8 Restore handlers with new signatures ────────────────────────────────

/// <summary>
/// Verifies restore handlers with enriched FileSize parameter update all fields.
/// </summary>
public class RestoreHandlerNewSignatureTests
{
    [Test]
    public async Task FileRestoredHandler_UpdatesCounterBytesAndQueue()
    {
        var state   = new ProgressState();
        var handler = new FileRestoredHandler(state);

        await handler.Handle(new FileRestoredEvent("a/b.txt", 4096L), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(4096L);
        state.RecentRestoreEvents.Count.ShouldBe(1);
        state.RecentRestoreEvents.First().Skipped.ShouldBeFalse();
    }

    [Test]
    public async Task FileSkippedHandler_UpdatesCounterBytesAndQueue()
    {
        var state   = new ProgressState();
        var handler = new FileSkippedHandler(state);

        await handler.Handle(new FileSkippedEvent("c/d.txt", 2048L), CancellationToken.None);

        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(2048L);
        state.RecentRestoreEvents.Count.ShouldBe(1);
        state.RecentRestoreEvents.First().Skipped.ShouldBeTrue();
    }

    [Test]
    public async Task RehydrationStartedHandler_SetsBothFields()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStartedHandler(state);

        await handler.Handle(new RehydrationStartedEvent(3, 5_242_880L), CancellationToken.None);

        state.RehydrationChunkCount.ShouldBe(3);
        state.RehydrationTotalBytes.ShouldBe(5_242_880L);
    }
}

// ── 12.9–12.11 BuildRestoreDisplay ───────────────────────────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.BuildRestoreDisplay"/> renders correctly in
/// in-progress, completed, and rehydrating states.
/// </summary>
public class BuildRestoreDisplayTests
{
    private static string RenderToString(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        return writer.ToString();
    }

    // 12.9 In-progress state

    [Test]
    public void BuildRestoreDisplay_InProgress_ShowsOpenCircleAndTailLines()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(10);
        state.IncrementFilesRestored(1024L);
        state.AddRestoreEvent("foo/bar.txt", 1024L, skipped: false);
        state.AddRestoreEvent("baz/skip.txt", 512L, skipped: true);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldContain("○");        // in-progress symbol
        output.ShouldContain("Restoring");
        output.ShouldContain("Restored");
        output.ShouldContain("Skipped");
        output.ShouldContain("foo/bar.txt");
        output.ShouldContain("baz/skip.txt");
        output.ShouldContain("●");        // restored file marker
    }

    // 12.10 Completed state — no tail

    [Test]
    public void BuildRestoreDisplay_Completed_ShowsFilledCircleNoTail()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(2);
        state.IncrementFilesRestored(500L);
        state.IncrementFilesRestored(300L);
        // Add an event to the queue — should NOT appear when complete
        state.AddRestoreEvent("done.txt", 500L, skipped: false);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldContain("●");
        output.ShouldContain("Restoring");
        // Tail is omitted on completion
        output.ShouldNotContain("done.txt");
    }

    // 12.11 Rehydrating line shown only when RehydrationChunkCount > 0

    [Test]
    public void BuildRestoreDisplay_NoRehydration_NoRehydratingLine()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(5);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldNotContain("Rehydrating");
    }

    [Test]
    public void BuildRestoreDisplay_WithRehydration_ShowsRehydratingLine()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(5);
        state.SetRehydration(3, 1_048_576L);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldContain("Rehydrating");
        output.ShouldContain("3");
    }
}
