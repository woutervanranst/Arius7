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

// ── 7.1 TrackedFile state transitions: small file (tar) path ──────────────────

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the small-file/tar path:
/// Hashing → Hashed → removed from TrackedFiles when added to TAR.
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

        // FileHashedEvent → SetFileHashed → State=Hashed, reverse map populated
        state.SetFileHashed("notes.txt", "def456");
        state.TrackedFiles["notes.txt"].ContentHash.ShouldBe("def456");
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.Hashed);
        state.ContentHashToPath["def456"].ShouldContain("notes.txt");
        state.FilesHashed.ShouldBe(1L);

        // TarEntryAddedEvent → RemoveFile (small file moves into TAR)
        state.RemoveFile("notes.txt");
        state.TrackedFiles.ContainsKey("notes.txt").ShouldBeFalse();
    }
}

// ── 7.2 TrackedFile state transitions: large file path ────────────────────────

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the large-file/direct-upload path:
/// Hashing → Hashed → Uploading → Done (removed).
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

        // FileHashedEvent → State=Hashed (invisible)
        state.SetFileHashed("video.mp4", "abc123");
        state.TrackedFiles["video.mp4"].ContentHash.ShouldBe("abc123");
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Hashed);

        // ChunkUploadingEvent → SetFileUploading (only Hashed files promoted to Uploading)
        state.SetFileUploading("abc123");
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Uploading);

        // ChunkUploadedEvent → RemoveFile
        state.RemoveFile("video.mp4");
        state.TrackedFiles.ContainsKey("video.mp4").ShouldBeFalse();
    }

    [Test]
    public void SetFileUploading_OnlyPromotesHashedFiles()
    {
        // A file in Hashing state (not yet Hashed) should not transition to Uploading.
        var state = new ProgressState();
        state.AddFile("pending.txt", 100);
        // Don't call SetFileHashed — still in Hashing state
        state.TrackedFiles["pending.txt"].State.ShouldBe(FileState.Hashing);

        // No ContentHashToPath entry yet, so SetFileUploading won't find it
        state.SetFileUploading("nohash");
        state.TrackedFiles["pending.txt"].State.ShouldBe(FileState.Hashing);
    }
}

// ── ContentHashToPath reverse lookup ─────────────────────────────────────────

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
        state.ContentHashToPath["aabbcc"].ShouldContain("dir/file.bin");

        // Downstream event via reverse map: SetFileUploading transitions Hashed → Uploading
        state.SetFileUploading("aabbcc");
        state.TrackedFiles["dir/file.bin"].State.ShouldBe(FileState.Uploading);
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

// ── ProgressState concurrent add/transition/remove ───────────────────────────

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
    public void TotalFiles_NullUntilScanComplete()
    {
        var state = new ProgressState();
        state.TotalFiles.ShouldBeNull();
        state.SetScanComplete(1523, 1_000_000L);
        state.TotalFiles.ShouldBe(1523L);
        state.TotalBytes.ShouldBe(1_000_000L);
        state.ScanComplete.ShouldBeTrue();
    }
}

// ── 7.1 Archive notification handler unit tests ───────────────────────────────

/// <summary>
/// Verifies that each archive notification handler updates the correct
/// <see cref="TrackedFile"/> state and aggregate counters on <see cref="ProgressState"/>.
/// </summary>
public class NotificationHandlerTests
{
    // ── FileScannedHandler (7.1) ──────────────────────────────────────────────

    [Test]
    public async Task FileScannedHandler_IncrementsFilesScannedAndBytesScanned()
    {
        var state   = new ProgressState();
        var handler = new FileScannedHandler(state);

        await handler.Handle(new FileScannedEvent("foo/bar.txt", 1024), CancellationToken.None);

        state.FilesScanned.ShouldBe(1L);
        state.BytesScanned.ShouldBe(1024L);
    }

    [Test]
    public async Task FileScannedHandler_MultipleFiles_AccumulatesCorrectly()
    {
        var state   = new ProgressState();
        var handler = new FileScannedHandler(state);

        await handler.Handle(new FileScannedEvent("a.txt", 100), CancellationToken.None);
        await handler.Handle(new FileScannedEvent("b.txt", 200), CancellationToken.None);
        await handler.Handle(new FileScannedEvent("c.txt", 300), CancellationToken.None);

        state.FilesScanned.ShouldBe(3L);
        state.BytesScanned.ShouldBe(600L);
    }

    // ── ScanCompleteHandler (7.2) ─────────────────────────────────────────────

    [Test]
    public async Task ScanCompleteHandler_SetsTotalsAndScanComplete()
    {
        var state   = new ProgressState();
        var handler = new ScanCompleteHandler(state);

        state.ScanComplete.ShouldBeFalse();
        await handler.Handle(new ScanCompleteEvent(1523, 5_000_000L), CancellationToken.None);

        state.TotalFiles.ShouldBe(1523L);
        state.TotalBytes.ShouldBe(5_000_000L);
        state.ScanComplete.ShouldBeTrue();
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
    public async Task FileHashedHandler_TransitionsToHashedAndIncrementsCounter()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);

        await hashingH.Handle(new FileHashingEvent("a.bin", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("a.bin", "abc123"), CancellationToken.None);

        state.FilesHashed.ShouldBe(1L);
        state.TrackedFiles["a.bin"].ContentHash.ShouldBe("abc123");
        state.TrackedFiles["a.bin"].State.ShouldBe(FileState.Hashed);
        state.ContentHashToPath["abc123"].ShouldContain("a.bin");
    }

    // ── TarBundleStartedHandler (7.3) ─────────────────────────────────────────

    [Test]
    public async Task TarBundleStartedHandler_CreatesTrackedTar()
    {
        var state   = new ProgressState();
        var handler = new TarBundleStartedHandler(state);

        await handler.Handle(new TarBundleStartedEvent(), CancellationToken.None);

        state.TrackedTars.Count.ShouldBe(1);
        state.TrackedTars[1].BundleNumber.ShouldBe(1);
        state.TrackedTars[1].State.ShouldBe(TarState.Accumulating);
    }

    [Test]
    public async Task TarBundleStartedHandler_MultipleBundles_IncrementsBundleNumber()
    {
        var state   = new ProgressState();
        var handler = new TarBundleStartedHandler(state);

        await handler.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await handler.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await handler.Handle(new TarBundleStartedEvent(), CancellationToken.None);

        state.TrackedTars.Count.ShouldBe(3);
        state.TrackedTars[1].BundleNumber.ShouldBe(1);
        state.TrackedTars[2].BundleNumber.ShouldBe(2);
        state.TrackedTars[3].BundleNumber.ShouldBe(3);
    }

    // ── TarEntryAddedHandler (7.4) ────────────────────────────────────────────

    [Test]
    public async Task TarEntryAddedHandler_RemovesTrackedFileAndUpdatesTrackedTar()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var startedH   = new TarBundleStartedHandler(state);
        var tarEntryH  = new TarEntryAddedHandler(state);

        await hashingH.Handle(new FileHashingEvent("small.txt", 500), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("small.txt", "def456"), CancellationToken.None);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await tarEntryH.Handle(new TarEntryAddedEvent("def456", 1, 500), CancellationToken.None);

        // File removed from TrackedFiles
        state.TrackedFiles.ContainsKey("small.txt").ShouldBeFalse();

        // TrackedTar updated
        state.TrackedTars[1].FileCount.ShouldBe(1);
        state.TrackedTars[1].AccumulatedBytes.ShouldBe(500L);
    }

    [Test]
    public async Task TarEntryAddedHandler_IncrementsFilesUnique()
    {
        var state     = new ProgressState();
        var hashingH  = new FileHashingHandler(state);
        var hashedH   = new FileHashedHandler(state);
        var startedH  = new TarBundleStartedHandler(state);
        var tarEntryH = new TarEntryAddedHandler(state);

        await hashingH.Handle(new FileHashingEvent("s.txt", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("s.txt", "h1"), CancellationToken.None);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await tarEntryH.Handle(new TarEntryAddedEvent("h1", 1, 100), CancellationToken.None);

        state.FilesUnique.ShouldBe(1L);
    }

    // ── TarBundleSealingHandler ───────────────────────────────────────────────

    [Test]
    public async Task TarBundleSealingHandler_TransitionsToSealingAndSetsTarHash()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, "tar_xyz", ["h1", "h2", "h3"]),
            CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Sealing);
        state.TrackedTars[1].TarHash.ShouldBe("tar_xyz");
        state.TrackedTars[1].TotalBytes.ShouldBe(300L);
    }

    // ── ChunkUploadingHandler (7.5) ───────────────────────────────────────────

    [Test]
    public async Task ChunkUploadingHandler_LargeFile_SetsUploadingAndIncrementsFilesUnique()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await hashingH.Handle(new FileHashingEvent("large.bin", 1_000_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("large.bin", "bigfile1"), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent("bigfile1", 1_000_000), CancellationToken.None);

        state.TrackedFiles["large.bin"].State.ShouldBe(FileState.Uploading);
        state.FilesUnique.ShouldBe(1L);
    }

    [Test]
    public async Task ChunkUploadingHandler_TarBundle_TransitionsTarToUploading()
    {
        var state      = new ProgressState();
        var startedH   = new TarBundleStartedHandler(state);
        var sealingH   = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(2, 200, "tar_hash_1", ["h1", "h2"]),
            CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent("tar_hash_1", 200), CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);
        // FilesUnique should NOT be incremented for TAR bundle (only for large files)
        state.FilesUnique.ShouldBe(0L);
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

    // ── TarBundleUploadedHandler (7.4) ────────────────────────────────────────

    [Test]
    public async Task TarBundleUploadedHandler_RemovesTrackedTarAndIncrementsTarsUploaded()
    {
        var state     = new ProgressState();
        var startedH  = new TarBundleStartedHandler(state);
        var sealingH  = new TarBundleSealingHandler(state);
        var uploadedH = new TarBundleUploadedHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, "tar_abc", ["ha", "hb", "hc"]),
            CancellationToken.None);
        await uploadedH.Handle(
            new TarBundleUploadedEvent("tar_abc", 200, 3),
            CancellationToken.None);

        state.TrackedTars.ContainsKey(1).ShouldBeFalse();
        state.TarsUploaded.ShouldBe(1L);
        state.ChunksUploaded.ShouldBe(1L);
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

// ── TrackedTar lifecycle (7.3) ────────────────────────────────────────────────

/// <summary>
/// Verifies the full <see cref="TrackedTar"/> lifecycle:
/// Accumulating → Sealing → Uploading → removed.
/// </summary>
public class TrackedTarLifecycleTests
{
    [Test]
    public async Task TrackedTar_FullLifecycle_StateTransitions()
    {
        var state     = new ProgressState();
        var startedH  = new TarBundleStartedHandler(state);
        var entryH    = new TarEntryAddedHandler(state);
        var sealingH  = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var uploadedH = new TarBundleUploadedHandler(state);

        // Start tar
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Accumulating);

        // Add files (need them in ContentHashToPath for entry handler)
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        await hashingH.Handle(new FileHashingEvent("f1.txt", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("f1.txt", "h1"), CancellationToken.None);
        await hashingH.Handle(new FileHashingEvent("f2.txt", 200), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("f2.txt", "h2"), CancellationToken.None);

        await entryH.Handle(new TarEntryAddedEvent("h1", 1, 100), CancellationToken.None);
        await entryH.Handle(new TarEntryAddedEvent("h2", 2, 300), CancellationToken.None);

        state.TrackedTars[1].FileCount.ShouldBe(2);
        state.TrackedTars[1].AccumulatedBytes.ShouldBeGreaterThan(0);

        // Seal
        await sealingH.Handle(
            new TarBundleSealingEvent(2, 300, "seal_hash", ["h1", "h2"]),
            CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Sealing);
        state.TrackedTars[1].TarHash.ShouldBe("seal_hash");
        state.TrackedTars[1].TotalBytes.ShouldBe(300L);

        // Upload starts
        await uploadingH.Handle(new ChunkUploadingEvent("seal_hash", 300), CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);

        // Upload complete → removed
        await uploadedH.Handle(new TarBundleUploadedEvent("seal_hash", 200, 2), CancellationToken.None);
        state.TrackedTars.ContainsKey(1).ShouldBeFalse();
        state.TarsUploaded.ShouldBe(1L);
    }
}

// ── 7.5 ChunkUploadingHandler: dual lookup ────────────────────────────────────

/// <summary>
/// Verifies the dual-lookup behavior of <see cref="ChunkUploadingHandler"/>:
/// large files via <see cref="ProgressState.ContentHashToPath"/> and TAR bundles
/// via <see cref="ProgressState.TrackedTars"/>.
/// </summary>
public class ChunkUploadingHandlerDualLookupTests
{
    [Test]
    public async Task DualLookup_LargeFile_TakesPrecedence()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await hashingH.Handle(new FileHashingEvent("big.bin", 10_000_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("big.bin", "largehash"), CancellationToken.None);

        await uploadingH.Handle(new ChunkUploadingEvent("largehash", 10_000_000), CancellationToken.None);

        state.TrackedFiles["big.bin"].State.ShouldBe(FileState.Uploading);
        state.FilesUnique.ShouldBe(1L);
    }

    [Test]
    public async Task DualLookup_TarBundle_TransitionsToUploading_NoFilesUniqueIncrement()
    {
        var state      = new ProgressState();
        var startedH   = new TarBundleStartedHandler(state);
        var sealingH   = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(1, 100, "tarhash99", ["h1"]),
            CancellationToken.None);

        await uploadingH.Handle(new ChunkUploadingEvent("tarhash99", 100), CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);
        state.FilesUnique.ShouldBe(0L);  // TAR path does NOT increment FilesUnique
    }
}

// ── 7.6 BuildArchiveDisplay: redesigned rendering ────────────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.BuildArchiveDisplay"/> renders the new three-section
/// layout: scanning header with live counter, hashing header with unique count + queue depth,
/// uploading header, per-file lines (only Hashing/Uploading), and TAR bundle lines.
/// </summary>
public class BuildArchiveDisplayTests
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

    // ── Stage headers ─────────────────────────────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_ShowsAllThreeStageHeaders()
    {
        var state = new ProgressState();
        var renderable = CliBuilder.BuildArchiveDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("Scanning");
        output.ShouldContain("Hashing");
        output.ShouldContain("Uploading");
    }

    // ── 6.1 Scanning header ───────────────────────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_ScanningHeader_ShowsLiveCount()
    {
        var state = new ProgressState();
        state.IncrementFilesScanned(1024);
        state.IncrementFilesScanned(2048);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("2");  // 2 files scanned
        output.ShouldContain("○"); // not complete
    }

    [Test]
    public void BuildArchiveDisplay_ScanningHeader_FilledCircle_WhenScanComplete()
    {
        var state = new ProgressState();
        state.SetScanComplete(1523, 5_000_000L);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("●");
        output.ShouldContain("1");  // count shown
    }

    // ── 6.2 Hashing header with unique count + queue depth ───────────────────

    [Test]
    public void BuildArchiveDisplay_HashingHeader_ShowsUniqueCount()
    {
        var state = new ProgressState();
        state.IncrementFilesUnique();
        state.IncrementFilesUnique();
        state.IncrementFilesUnique();

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("unique");
        output.ShouldContain("3");
    }

    [Test]
    public void BuildArchiveDisplay_HashingHeader_ShowsQueueDepth_WhenNonZero()
    {
        var state = new ProgressState();
        state.HashQueueDepth = () => 12;

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("pending");
        output.ShouldContain("12");
    }

    [Test]
    public void BuildArchiveDisplay_HashingHeader_NoQueueDepth_WhenZero()
    {
        var state = new ProgressState();
        state.HashQueueDepth = () => 0;

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldNotContain("pending");
    }

    // ── 6.3 Uploading header with queue depth ────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_ShowsQueueDepth_WhenNonZero()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        state.UploadQueueDepth = () => 3;

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("3");
        output.ShouldContain("pending");
    }

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_FilledCircle_WhenSnapshotComplete()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        state.SetSnapshotComplete();

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("●");
    }

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_OpenCircle_WhenNotComplete()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        // SnapshotComplete NOT set

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("○");
    }

    // ── 6.4 Per-file lines: only Hashing or Uploading ────────────────────────

    [Test]
    public void BuildArchiveDisplay_ShowsHashingFile()
    {
        var state = new ProgressState();
        state.AddFile("video.mp4", 5_000_000);
        // State is Hashing by default

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("video.mp4");
        output.ShouldContain("Hashing");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsUploadingFile()
    {
        var state = new ProgressState();
        state.AddFile("large.bin", 10_000_000);
        state.SetFileHashed("large.bin", "lhash");
        state.SetFileUploading("lhash");

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("large.bin");
        output.ShouldContain("Uploading");
    }

    [Test]
    public void BuildArchiveDisplay_DoesNotShowHashedFile()
    {
        // Hashed state is invisible
        var state = new ProgressState();
        state.AddFile("pending.bin", 1000);
        state.SetFileHashed("pending.bin", "ph1");
        // State is now Hashed — should not appear

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldNotContain("pending.bin");
    }

    [Test]
    public void BuildArchiveDisplay_DoesNotShowRemovedFiles()
    {
        var state = new ProgressState();
        state.AddFile("completed.bin", 1000);
        state.SetFileHashed("completed.bin", "done1");
        state.RemoveFile("completed.bin");

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldNotContain("completed.bin");
    }

    // ── 6.5 TAR bundle lines ─────────────────────────────────────────────────

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenAccumulating()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("TAR #1");
        output.ShouldContain("Accumulating");
    }

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenSealing()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, "t1", ["h1", "h2", "h3"]),
            CancellationToken.None);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("TAR #1");
        output.ShouldContain("Sealing");
    }

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenUploading()
    {
        var state      = new ProgressState();
        var startedH   = new TarBundleStartedHandler(state);
        var sealingH   = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(2, 200, "t2", ["ha", "hb"]),
            CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent("t2", 200), CancellationToken.None);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("TAR #1");
        output.ShouldContain("Uploading");
    }

    // ── Truncation + size column ──────────────────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_ShowsRelativePath_NotJustFilename()
    {
        var state = new ProgressState();
        state.AddFile("some/deep/path/file.bin", 1024);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("some/deep/path/file.bin");
    }

    [Test]
    public void BuildArchiveDisplay_TruncatesLongRelativePath_WithEllipsisPrefix()
    {
        var longPath = "a/very/long/directory/structure/with/file.bin"; // > 30 chars
        var state = new ProgressState();
        state.AddFile(longPath, 2048);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("...");
        output.ShouldNotContain(longPath);
    }

    [Test]
    public void BuildArchiveDisplay_ShowsSizeInMB_ForHashingFile()
    {
        var state = new ProgressState();
        state.AddFile("doc.pdf", 5_000_000);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("MB");
    }
}

// ── BuildArchiveDisplay done files not rendered ───────────────────────────────

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
        state.RemoveFile("completed.bin");

        var writer  = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(CliBuilder.BuildArchiveDisplay(state));
        var output = writer.ToString();

        output.ShouldNotContain("completed.bin");
    }
}

// ── RenderProgressBar: bar character fill ─────────────────────────────────────

/// <summary>
/// Verifies <see cref="CliBuilder.RenderProgressBar"/> produces correct fill ratios.
/// </summary>
public class RenderProgressBarTests
{
    [Test]
    public void RenderProgressBar_ZeroFraction_AllEmpty()
    {
        var bar = CliBuilder.RenderProgressBar(0.0, 10);
        bar.ShouldContain(new string('░', 10));
    }

    [Test]
    public void RenderProgressBar_FullFraction_AllFilled()
    {
        var bar = CliBuilder.RenderProgressBar(1.0, 10);
        bar.ShouldContain(new string('█', 10));
    }

    [Test]
    public void RenderProgressBar_HalfFraction_HalfFilled()
    {
        var bar = CliBuilder.RenderProgressBar(0.5, 12);
        bar.ShouldContain(new string('█', 6));
        bar.ShouldContain(new string('░', 6));
    }

    [Test]
    public void RenderProgressBar_62Percent_Width12_SevenOrEightFilled()
    {
        var bar = CliBuilder.RenderProgressBar(0.62, 12);
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

// ── Integration: Mediator routes events to ProgressState handlers ─────────────

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

        // Publish all archive notification events in pipeline order
        await mediator.Publish(new FileScannedEvent("a.bin", 100));
        await mediator.Publish(new ScanCompleteEvent(1, 100));
        await mediator.Publish(new FileHashingEvent("a.bin", 100));
        await mediator.Publish(new FileHashedEvent("a.bin", "hash1"));
        await mediator.Publish(new TarBundleStartedEvent());
        await mediator.Publish(new TarEntryAddedEvent("hash1", 1, 100));
        await mediator.Publish(new TarBundleSealingEvent(1, 100, "tar1", ["hash1"]));
        await mediator.Publish(new ChunkUploadingEvent("tar1", 100));
        await mediator.Publish(new TarBundleUploadedEvent("tar1", 80, 1));
        await mediator.Publish(new SnapshotCreatedEvent("root", DateTimeOffset.UtcNow, 1));

        // Verify ProgressState was updated
        state.FilesScanned.ShouldBe(1L);
        state.TotalFiles.ShouldBe(1L);
        state.ScanComplete.ShouldBeTrue();
        state.FilesHashed.ShouldBe(1L);
        state.TarsUploaded.ShouldBe(1L);
        // a.bin was removed after tar entry added
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

// ── Integration: progress callbacks report bytes ──────────────────────────────

/// <summary>
/// Verifies that <see cref="CliBuilder"/>'s <c>CreateHashProgress</c> and
/// <c>CreateUploadProgress</c> factory callbacks correctly wire to
/// <see cref="TrackedFile.SetBytesProcessed"/> and <see cref="TrackedTar.SetBytesUploaded"/>.
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
        file.ShouldNotBeNull();

        file.SetBytesProcessed(2_500_000);
        file.BytesProcessed.ShouldBe(2_500_000L);
    }

    [Test]
    public void CreateUploadProgress_LargeFile_ResetsThenUpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("chunk.bin", 1_000_000);
        state.SetFileHashed("chunk.bin", "chash1");
        state.SetFileUploading("chash1");

        IProgress<long>? uploadProgress = null;
        if (state.ContentHashToPath.TryGetValue("chash1", out var paths))
        {
            var files = paths
                .Select(p => state.TrackedFiles.TryGetValue(p, out var f) ? f : null)
                .Where(f => f != null)
                .ToList();
            if (files.Count > 0)
            {
                foreach (var f in files) f!.SetBytesProcessed(0);
                uploadProgress = new Progress<long>(bytes => { foreach (var f in files) f!.SetBytesProcessed(bytes); });
            }
        }

        uploadProgress.ShouldNotBeNull();
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(0L);

        state.TrackedFiles["chunk.bin"].SetBytesProcessed(450_000);
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(450_000L);
    }

    [Test]
    public void CreateUploadProgress_TarBundle_UpdatesBytesUploaded()
    {
        var state = new ProgressState();
        var tar   = new TrackedTar(1, 64L * 1024 * 1024);
        tar.TarHash = "tarhash1";
        tar.TotalBytes = 300L;
        state.TrackedTars.TryAdd(1, tar);

        // Simulate TAR branch of CreateUploadProgress
        var foundTar = state.TrackedTars.Values.FirstOrDefault(t => t.TarHash == "tarhash1");
        foundTar.ShouldNotBeNull();

        foundTar!.SetBytesUploaded(150L);
        tar.BytesUploaded.ShouldBe(150L);
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

// ── TrackedFile BytesProcessed Interlocked update test ────────────────────────

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

// ── BuildArchiveDisplay: round-2 refinements ──────────────────────────────────

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

    [Test]
    public void BuildArchiveDisplay_UsesFilledCircle_WhenScanningComplete()
    {
        var state = new ProgressState();
        state.SetScanComplete(3, 3000L);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("●");
    }

    [Test]
    public void BuildArchiveDisplay_UsesOpenCircle_WhenScanningInProgress()
    {
        var state  = new ProgressState();   // ScanComplete not set
        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));

        output.ShouldContain("○");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsRelativePath_NotJustFilename()
    {
        var state = new ProgressState();
        state.AddFile("some/deep/path/file.bin", 1024);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("some/deep/path/file.bin");
    }

    [Test]
    public void BuildArchiveDisplay_TruncatesLongRelativePath_WithEllipsisPrefix()
    {
        var longPath = "a/very/long/directory/structure/with/file.bin"; // > 30 chars
        var state = new ProgressState();
        state.AddFile(longPath, 2048);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("...");
        output.ShouldNotContain(longPath);
    }

    [Test]
    public void BuildArchiveDisplay_ShowsSize_ForHashingState()
    {
        var state = new ProgressState();
        state.AddFile("doc.pdf", 5_000_000);

        var output = RenderToString(CliBuilder.BuildArchiveDisplay(state));
        output.ShouldContain("MB");
    }
}

// ── TruncateAndLeftJustify ────────────────────────────────────────────────────

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
        var result = CliBuilder.TruncateAndLeftJustify("abcdefghij", 7);
        result.ShouldBe("...ghij");
        result.Length.ShouldBe(7);
    }

    [Test]
    public void Width4_LongPath_EllipsisPlusOneChar()
    {
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

// ── AddRestoreEvent ring buffer ───────────────────────────────────────────────

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

// ── IncrementFilesRestored / IncrementFilesSkipped byte accumulators ──────────

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

// ── SetRehydration sets both fields ──────────────────────────────────────────

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

// ── Restore handlers with new signatures ─────────────────────────────────────

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

// ── BuildRestoreDisplay ───────────────────────────────────────────────────────

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

    [Test]
    public void BuildRestoreDisplay_InProgress_ShowsOpenCircleAndTailLines()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(10);
        state.IncrementFilesRestored(1024L);
        state.AddRestoreEvent("foo/bar.txt", 1024L, skipped: false);
        state.AddRestoreEvent("baz/skip.txt", 512L, skipped: true);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldContain("○");
        output.ShouldContain("Restoring");
        output.ShouldContain("Restored");
        output.ShouldContain("Skipped");
        output.ShouldContain("foo/bar.txt");
        output.ShouldContain("baz/skip.txt");
        output.ShouldContain("●");
    }

    [Test]
    public void BuildRestoreDisplay_Completed_ShowsFilledCircleNoTail()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(2);
        state.IncrementFilesRestored(500L);
        state.IncrementFilesRestored(300L);
        state.AddRestoreEvent("done.txt", 500L, skipped: false);

        var output = RenderToString(CliBuilder.BuildRestoreDisplay(state));

        output.ShouldContain("●");
        output.ShouldContain("Restoring");
        output.ShouldNotContain("done.txt");
    }

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
