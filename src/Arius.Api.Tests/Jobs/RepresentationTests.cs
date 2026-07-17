using Arius.Api.Hubs;
using Arius.Api.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

public class RepresentationTests
{
    [Test]
    public async Task Pointer_heavy_archive_does_not_underflow_TotalNewBytes()
    {
        // Steady state: 1000 pointer-only deduped files (scanned as 0 bytes, deduped at full size)
        // + one new 100 MB file that actually uploads.
        var s = new JobSink();
        s.SetTotals(files: 1001, bytes: 100_000_000);      // scan excludes pointer-only (0 bytes each)
        s.AddDeduped(original: 1_000_000_000);             // pointer-only dedup at full size (> total)
        s.AddQueuedNew(100_000_000);                       // one new chunk queued for upload
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), stored: 60_000_000, original: 100_000_000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        // Additive new-bytes, NOT total - deduped (which would be Max(0, 100M - 1000M) = 0):
        await Assert.That(snap.TotalNewBytes).IsEqualTo(100_000_000L);
        await Assert.That(snap.Pct).IsEqualTo(100);        // uploaded 100M of 100M new
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
        await Assert.That(snap.ChunksTotal).IsEqualTo(427);            // authoritative, includes needs-rehydration
        // sanity: the four buckets sum to the authoritative total
        await Assert.That(snap.ChunksAvailable + snap.ChunksRehydrated + snap.ChunksNeedingRehydration + snap.ChunksPending)
            .IsEqualTo(snap.ChunksTotal);
    }
}
