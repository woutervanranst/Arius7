using Arius.Cli.Commands.Archive;
using Arius.Core.Features.ArchiveCommand;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies that archive notification handlers update the correct
/// <see cref="TrackedFile"/> state and aggregate counters on <see cref="ProgressState"/>.
/// </summary>
public class NotificationHandlerTests
{
    [Test]
    public async Task FileScannedHandler_IncrementsFilesScannedAndBytesScanned()
    {
        var state   = new ProgressState();
        var handler = new FileScannedHandler(state);

        await handler.Handle(new FileScannedEvent(RelativePath.Parse("foo/bar.txt"), 1024), CancellationToken.None);

        state.FilesScanned.ShouldBe(1L);
        state.BytesScanned.ShouldBe(1024L);
    }

    [Test]
    public async Task FileScannedHandler_MultipleFiles_AccumulatesCorrectly()
    {
        var state   = new ProgressState();
        var handler = new FileScannedHandler(state);

        await handler.Handle(new FileScannedEvent(RelativePath.Parse("a.txt"), 100), CancellationToken.None);
        await handler.Handle(new FileScannedEvent(RelativePath.Parse("b.txt"), 200), CancellationToken.None);
        await handler.Handle(new FileScannedEvent(RelativePath.Parse("c.txt"), 300), CancellationToken.None);

        state.FilesScanned.ShouldBe(3L);
        state.BytesScanned.ShouldBe(600L);
    }

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

    [Test]
    public async Task FileHashingHandler_AddsTrackedFile()
    {
        var state   = new ProgressState();
        var handler = new FileHashingHandler(state);
        var path    = RelativePath.Parse("foo/bar.bin");

        await handler.Handle(new FileHashingEvent(path, 1024), CancellationToken.None);

        state.TrackedFiles.ContainsKey(path).ShouldBeTrue();
        state.TrackedFiles[path].State.ShouldBe(FileState.Hashing);
        state.TrackedFiles[path].TotalBytes.ShouldBe(1024L);
    }

    [Test]
    public async Task FileHashedHandler_TransitionsToHashedAndIncrementsCounter()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        var path     = RelativePath.Parse("a.bin");

        await hashingH.Handle(new FileHashingEvent(path, 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('a'), false, false), CancellationToken.None);

        state.FilesHashed.ShouldBe(1L);
        state.TrackedFiles[path].ContentHash.ShouldBe(FakeContentHash('a'));
        state.TrackedFiles[path].State.ShouldBe(FileState.Hashed);
        state.ContentHashToPath[FakeContentHash('a')].ShouldContain(path);
    }

    [Test]
    public async Task EntryExcludedHandler_IncrementsEntriesExcluded()
    {
        var state    = new ProgressState();
        var handler  = new EntryExcludedHandler(state);

        await handler.Handle(new EntryExcludedEvent(RelativePath.Parse("@eaDir"), ExclusionReason.ExcludedByName), CancellationToken.None);
        await handler.Handle(new EntryExcludedEvent(RelativePath.Parse("thumbs.db"), ExclusionReason.ExcludedByName), CancellationToken.None);

        state.EntriesExcluded.ShouldBe(2L);
    }

    [Test]
    public async Task FileSkippedHandler_WhileStillHashing_RemovesTrackedFileAndIncrementsHashSkipCounter()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var skippedH = new FileSkippedHandler(state);
        var path     = RelativePath.Parse("hashing.bin");

        await hashingH.Handle(new FileHashingEvent(path, 100), CancellationToken.None);
        await skippedH.Handle(new FileSkippedEvent(path), CancellationToken.None);

        state.FilesSkippedHashing.ShouldBe(1L);
        state.TrackedFiles.ContainsKey(path).ShouldBeFalse();
    }

    [Test]
    public async Task FileSkippedHandler_AfterHashing_RemovesTrackedFileWithoutIncrementingHashSkipCounter()
    {
        var state    = new ProgressState();
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        var skippedH = new FileSkippedHandler(state);
        var path     = RelativePath.Parse("tar-input.bin");

        await hashingH.Handle(new FileHashingEvent(path, 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('2'), false, false), CancellationToken.None);
        await skippedH.Handle(new FileSkippedEvent(path), CancellationToken.None);

        state.FilesHashed.ShouldBe(1L);
        state.FilesSkippedHashing.ShouldBe(0L);
        state.TrackedFiles.ContainsKey(path).ShouldBeFalse();
    }

    [Test]
    public async Task FileSkippedHandler_DuringUpload_RemovesTrackedFileWithoutIncrementingHashSkipCounter()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var skippedH   = new FileSkippedHandler(state);
        var path       = RelativePath.Parse("large.bin");

        await hashingH.Handle(new FileHashingEvent(path, 1_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('3'), false, false), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent(FakeChunkHash('3'), 1_000), CancellationToken.None);
        await skippedH.Handle(new FileSkippedEvent(path), CancellationToken.None);

        state.FilesUnique.ShouldBe(1L);
        state.FilesSkippedHashing.ShouldBe(0L);
        state.TrackedFiles.ContainsKey(path).ShouldBeFalse();
    }

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

    [Test]
    public async Task TarEntryAddedHandler_RemovesTrackedFileAndUpdatesTrackedTar()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var startedH   = new TarBundleStartedHandler(state);
        var tarEntryH  = new TarEntryAddedHandler(state);
        var path       = RelativePath.Parse("small.txt");

        await hashingH.Handle(new FileHashingEvent(path, 500), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('b'), false, false), CancellationToken.None);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await tarEntryH.Handle(new TarEntryAddedEvent(FakeContentHash('b'), 1, 500), CancellationToken.None);

        state.TrackedFiles.ContainsKey(path).ShouldBeFalse();
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
        var path      = RelativePath.Parse("s.txt");

        await hashingH.Handle(new FileHashingEvent(path, 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('c'), false, false), CancellationToken.None);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await tarEntryH.Handle(new TarEntryAddedEvent(FakeContentHash('c'), 1, 100), CancellationToken.None);

        state.FilesUnique.ShouldBe(1L);
    }

    [Test]
    public async Task TarBundleSealingHandler_TransitionsToSealingAndSetsTarHash()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, 450, FakeChunkHash('d'), [FakeContentHash('a'), FakeContentHash('b'), FakeContentHash('c')]),
            CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Sealing);
        state.TrackedTars[1].TarHash.ShouldBe(FakeChunkHash('d'));
        // TotalBytes tracks the tar archive size (TarByteSize=450), not the uncompressed sum (300).
        state.TrackedTars[1].TotalBytes.ShouldBe(450L);
    }

    [Test]
    public async Task ChunkUploadingHandler_LargeFile_SetsUploadingAndIncrementsFilesUnique()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var path       = RelativePath.Parse("large.bin");

        await hashingH.Handle(new FileHashingEvent(path, 1_000_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('e'), false, false), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent(FakeChunkHash('e'), 1_000_000), CancellationToken.None);

        state.TrackedFiles[path].State.ShouldBe(FileState.Uploading);
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
            new TarBundleSealingEvent(2, 200, 200, FakeChunkHash('f'), [FakeContentHash('a'), FakeContentHash('b')]),
            CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent(FakeChunkHash('f'), 200), CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);
        state.FilesUnique.ShouldBe(0L);
    }

    [Test]
    public async Task ChunkUploadedHandler_RemovesFileAndIncrementsChunksUploaded()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var uploadedH  = new ChunkUploadedHandler(state);
        var path       = RelativePath.Parse("data.bin");

        await hashingH.Handle(new FileHashingEvent(path, 5000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent(path, FakeContentHash('9'), false, false), CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent(FakeChunkHash('9'), 5000), CancellationToken.None);
        await uploadedH.Handle(new ChunkUploadedEvent(FakeChunkHash('9'), 4000, 5000), CancellationToken.None);

        state.TrackedFiles.ContainsKey(path).ShouldBeFalse();
        state.ChunksUploaded.ShouldBe(1L);
        state.BytesUploaded.ShouldBe(4000L);
    }

    [Test]
    public async Task TarBundleUploadedHandler_RemovesTrackedTarAndIncrementsTarsUploaded()
    {
        var state     = new ProgressState();
        var startedH  = new TarBundleStartedHandler(state);
        var sealingH  = new TarBundleSealingHandler(state);
        var uploadedH = new TarBundleUploadedHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, 300, FakeChunkHash('a'), [FakeContentHash('d'), FakeContentHash('e'), FakeContentHash('f')]),
            CancellationToken.None);
        await uploadedH.Handle(
            new TarBundleUploadedEvent(FakeChunkHash('a'), 200, 3),
            CancellationToken.None);

        state.TrackedTars.ContainsKey(1).ShouldBeFalse();
        state.TarsUploaded.ShouldBe(1L);
        state.ChunksUploaded.ShouldBe(1L);
    }

    [Test]
    public async Task SnapshotCreatedHandler_SetsSnapshotComplete()
    {
        var state   = new ProgressState();
        var handler = new SnapshotCreatedHandler(state);

        state.SnapshotComplete.ShouldBeFalse();
        await handler.Handle(new SnapshotCreatedEvent(FakeFileTreeHash('b'), DateTimeOffset.UtcNow, 10), CancellationToken.None);

        state.SnapshotComplete.ShouldBeTrue();
    }
}
