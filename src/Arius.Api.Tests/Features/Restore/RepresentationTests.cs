using Arius.Api.Features.Restore;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Features.Restore;

public class RepresentationTests
{
    [Test]
    public async Task Pointer_heavy_archive_does_not_underflow_TotalNewBytes()
    {
        // 1000 pointer-only files (scanned as 0 bytes, deduplicated at full size) and one new 100 MB file.
        var s = new JobSink();
        s.SetTotals(files: 1001, bytes: 100_000_000);
        s.AddDeduped(original: 1_000_000_000);             // exceeds the total
        s.AddQueuedNew(100_000_000);
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), stored: 60_000_000, original: 100_000_000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        // Not total − deduped, which would clamp to 0.
        await Assert.That(snap.TotalNewBytes).IsEqualTo(100_000_000L);
        await Assert.That(snap.Pct).IsEqualTo(100);        // 100 MB of 100 MB new bytes uploaded
    }

    [Test]
    public async Task ChunkResolutionCompleteForwarder_sets_authoritative_chunk_total()
    {
        var s = new JobSink();
        await new ChunkResolutionCompleteForwarder(s).Handle(
            new Arius.Core.Features.RestoreCommand.ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000), default);
        await new RehydrationStatusForwarder(s).Handle(
            new Arius.Core.Features.RestoreCommand.RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0), default);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.ChunksTotal).IsEqualTo(427);            // includes the chunks needing rehydration
        // the four buckets add up to the total
        await Assert.That(snap.ChunksAvailable + snap.ChunksRehydrated + snap.ChunksNeedingRehydration + snap.ChunksPending)
            .IsEqualTo(snap.ChunksTotal);
    }
}
