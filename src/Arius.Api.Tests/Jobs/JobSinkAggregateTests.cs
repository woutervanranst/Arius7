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
        s.AddUploaded(stored: 400, original: 2000);
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
