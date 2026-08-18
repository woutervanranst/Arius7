using Arius.Api.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

/// <summary>
/// Locks down the archive progress/ETA fixes derived from the arius-20260721.txt [ETA] trace:
///  A. pct is the monotonic filled fraction (uploaded+deduped)/total — the pill/list stop running backwards.
///  B. the ETA never spikes (drops total−deduped as the denominator) and becomes exact/non-upper-bound once
///     routing completes.
///  C. the reported "sustained" throughput is the binding term's rate — never the stale/inflated hash rate at
///     the end of an upload-bound job.
/// </summary>
public class JobSinkProgressFixesTests
{
    private static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

    // ── Fix A: monotonic pct ────────────────────────────────────────────────
    [Test]
    public async Task Archive_pct_is_uploaded_plus_deduped_over_total()
    {
        var s = new JobSink();
        s.SetTotals(files: 1, bytes: 1000);
        s.AddDeduped(original: 600);
        s.AddUploaded(Chunk('a'), stored: 0, original: 100);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.Pct).IsEqualTo(70);   // (100 + 600) / 1000
    }

    [Test]
    public async Task Archive_pct_never_regresses_when_new_work_is_discovered()
    {
        // The backwards-bar reproduction: uploaded is flat while the queued-new total grows (as more
        // chunks start uploading). The OLD pct = uploaded/totalNew regressed 90 → 36; the new pct must not.
        var s = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddQueuedNew(200_000_000);
        s.AddUploaded(Chunk('1'), stored: 0, original: 180_000_000);
        var p1 = s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct;

        s.AddQueuedNew(300_000_000);   // totalNew jumps 200M → 500M
        var p2 = s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct;

        await Assert.That(p2).IsGreaterThanOrEqualTo(p1);
    }

    // ── Fix B: ETA no longer spikes; routing-complete makes it exact ─────────
    [Test]
    public async Task Eta_before_routing_uses_queued_new_bytes_not_total_minus_deduped()
    {
        // Mirrors the trace right after scan completes: total known, deduped lags badly, only a little is
        // actually queued-new. total−deduped (the OLD denominator) produced the 58 h spike.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddDeduped(original: 600_000_000);   // total − deduped = 400 MB (loose)
        s.AddQueuedNew(10_000_000);            // only 10 MB truly queued new
        s.AddHashed(1_000_000_000);            // hashing complete → hash term contributes nothing

        s.SampleForEta(t0);
        s.AddUploaded(Chunk('2'), stored: 0, original: 1_000_000);   // 1 MB over 1 s = 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        var eta = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;
        // NEW: (totalNew 10M − 1M) / 1 MB/s ≈ 9 s.   OLD: (400M − 1M) / 1 MB/s ≈ 399 s.
        await Assert.That(eta!.Value).IsBetween(8, 10);
    }

    [Test]
    public async Task Routing_complete_makes_eta_exact_and_not_an_upper_bound()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddQueuedNew(5_000_000);             // queued-so-far underestimates the real new-byte total
        s.SampleForEta(t0);
        s.AddUploaded(Chunk('3'), stored: 0, original: 1_000_000);   // 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        // Before routing completes the estimate is provisional (upper bound).
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsUpperBound).IsTrue();

        s.SetNewByteTotal(20_000_000);         // routing done: the exact new-byte total is 20 MB
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(18, 20);   // (20M − 1M) / 1 MB/s ≈ 19 s
        await Assert.That(snap.EtaIsUpperBound).IsFalse();
    }

    // ── Fix C: throughput is the binding term's rate, never the stale hash rate ──
    [Test]
    public async Task Throughput_at_upload_completion_is_transfer_rate_not_stale_hash_rate()
    {
        // Reproduces the 16 GB/s reading: the hash EMA is inflated by a huge instantaneous jump and never
        // decays; at eta==0 the OLD tie-break surfaced it as the throughput. It must report the transfer rate.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // 1 GB "hashed" in 1 s → ~1 GB/s hash EMA
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(10_000_000);
        s.AddUploaded(Chunk('4'), stored: 0, original: 10_000_000);   // all 10 MB over 1 s = 10 MB/s
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.EtaSeconds).IsEqualTo(0L);                          // upload complete
        await Assert.That(snap.ThroughputBytesPerSec).IsLessThan(100_000_000.0);  // NOT the ~1 GB/s hash rate
        await Assert.That(snap.ThroughputBytesPerSec).IsGreaterThan(1_000_000.0); // ~10 MB/s transfer
    }
}
