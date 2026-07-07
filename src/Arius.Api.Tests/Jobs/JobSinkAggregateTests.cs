using Arius.Api.Hubs;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

public class JobSinkAggregateTests
{
    private static JobSink NewArchiveSink() => new();   // inert (no hub) — aggregation is hub-independent

    [Test]
    public async Task Byte_layers_and_dedup_accumulate_as_original_bytes()
    {
        var s = NewArchiveSink();
        s.SetTotals(files: 3, bytes: 3000);
        s.AddScanned(3000);
        s.AddHashed(3000);
        s.AddUploaded(ChunkHash.Parse(new string('c', 64)), stored: 400, original: 2000);
        s.AddDeduped(original: 1000);
        s.AddQueuedNew(2000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalBytes).IsEqualTo(3000L);
        await Assert.That(snap.ScannedBytes).IsEqualTo(3000L);
        await Assert.That(snap.HashedBytes).IsEqualTo(3000L);
        await Assert.That(snap.UploadedBytes).IsEqualTo(2000L);   // original units, not stored
        await Assert.That(snap.DedupedBytes).IsEqualTo(1000L);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(2000L);   // additive queued-new bytes
    }

    [Test]
    public async Task Streaming_upload_progress_credits_continuously_without_double_counting_on_completion()
    {
        var s = NewArchiveSink();
        var chunk = ChunkHash.Parse(new string('e', 64));

        // Streaming reports are CUMULATIVE per chunk; uploadedBytes rises by the delta each time.
        s.ReportUploadStreamed(chunk, 30);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).UploadedBytes).IsEqualTo(30L);
        s.ReportUploadStreamed(chunk, 100);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).UploadedBytes).IsEqualTo(100L);

        // Completion reconciles to the final original size — it does NOT re-add the already-streamed bytes.
        s.AddUploaded(chunk, stored: 60, original: 100);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).UploadedBytes).IsEqualTo(100L);

        // A chunk that never streamed still credits its full size on completion (scripted / non-streaming path).
        s.AddUploaded(ChunkHash.Parse(new string('f', 64)), stored: 20, original: 50);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).UploadedBytes).IsEqualTo(150L);
    }

    [Test]
    public async Task Streaming_restore_progress_credits_continuously_without_double_counting()
    {
        var s = new JobSink();
        s.SetRestoreTotals(files: 2, bytes: 300);

        // Large file "a" downloads in increments (cumulative), then completes.
        s.ReportRestoreStreamed("a", 40);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).BytesRestored).IsEqualTo(40L);
        s.ReportRestoreStreamed("a", 100);
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).BytesRestored).IsEqualTo(100L);

        // Completion reconciles to the final size (same key) — does NOT re-add the streamed bytes.
        s.AddRestored("a", 100);
        var snap1 = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap1.BytesRestored).IsEqualTo(100L);
        await Assert.That(snap1.FilesRestored).IsEqualTo(1L);

        // Small tar-bundle file "b" never streamed — credited fully on completion.
        s.AddRestored("b", 200);
        var snap2 = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap2.BytesRestored).IsEqualTo(300L);
        await Assert.That(snap2.FilesRestored).IsEqualTo(2L);
    }

    [Test]
    public async Task Snapshot_carries_the_live_status()
    {
        var s = new JobSink();
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).Status).IsEqualTo("running"); // default mirrors the initial DB status
        s.SetStatus("rehydrating");
        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).Status).IsEqualTo("rehydrating");
    }

    [Test]
    public async Task Tar_uploaded_bytes_use_remembered_uncompressed_size()
    {
        var s = NewArchiveSink();
        var tar = ChunkHash.Parse(new string('a', 64));
        s.RememberTar(tar, uncompressed: 5000);
        s.AddUploadedTar(tar);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.UploadedBytes).IsEqualTo(5000L);
    }

    [Test]
    public async Task Archive_forwarders_populate_byte_layers()
    {
        var s = new JobSink();
        await new ScanCompleteForwarder(s).Handle(new ScanCompleteEvent(2, 3000), default);
        await new FileScannedForwarder(s).Handle(new FileScannedEvent(RelativePath.Parse("a"), 2000), default);
        await new FileHashingForwarder(s).Handle(new FileHashingEvent(RelativePath.Parse("a"), 2000), default);
        await new FileDedupedForwarder(s).Handle(new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), 1000), default);
        await new ChunkUploadingForwarder(s).Handle(new ChunkUploadingEvent(ChunkHash.Parse(new string('d', 64)), 2000), default);
        await new ChunkUploadedForwarder(s).Handle(new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), 300, 2000), default);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalBytes).IsEqualTo(3000L);
        await Assert.That(snap.ScannedBytes).IsEqualTo(2000L);
        await Assert.That(snap.UploadedBytes).IsEqualTo(2000L);
        await Assert.That(snap.DedupedBytes).IsEqualTo(1000L);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(2000L);
    }

    [Test]
    public async Task Restore_forwarders_populate_restore_fields_and_pct()
    {
        var s = new JobSink();
        await new TreeTraversalCompleteForwarder(s).Handle(new TreeTraversalCompleteEvent(4, 1000), default);
        await new RehydrationStatusForwarder(s).Handle(new RehydrationStatusEvent(Available: 2, Rehydrated: 1, NeedsRehydration: 1, Pending: 0), default);
        await new FileRestoredForwarder(s).Handle(new FileRestoredEvent(RelativePath.Parse("a"), 300), default);
        await new FileRestoredForwarder(s).Handle(new FileRestoredEvent(RelativePath.Parse("b"), 200), default);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.RestoreTotalFiles).IsEqualTo(4L);
        await Assert.That(snap.RestoreTotalBytes).IsEqualTo(1000L);
        await Assert.That(snap.FilesRestored).IsEqualTo(2L);
        await Assert.That(snap.BytesRestored).IsEqualTo(500L);
        await Assert.That(snap.ChunksAvailable).IsEqualTo(2);
        await Assert.That(snap.ChunksRehydrated).IsEqualTo(1);
        await Assert.That(snap.ChunksNeedingRehydration).IsEqualTo(1);
        await Assert.That(snap.ChunksPending).IsEqualTo(0);
        await Assert.That(snap.Pct).IsEqualTo(50);
    }
}
