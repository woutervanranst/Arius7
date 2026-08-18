using Arius.Api.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

/// <summary>
/// Locks down the archive progress/ETA contract:
///  A. pct is the monotonic filled fraction (uploaded+deduped)/total, so the pill and list never run backwards.
///  B. the ETA is driven by the queued-new bytes and becomes exact once routing completes.
///  C. the reported "sustained" throughput is the binding term's rate, never a stale hash rate.
/// </summary>
public class JobSinkProgressFixesTests
{
    private static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

    // ── Monotonic pct ───────────────────────────────────────────────────────
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
        // Uploaded is flat while the queued-new total grows as more chunks start uploading.
        var s = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddQueuedNew(200_000_000);
        s.AddUploaded(Chunk('1'), stored: 0, original: 180_000_000);
        var p1 = s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct;

        s.AddQueuedNew(300_000_000);   // totalNew jumps 200M → 500M
        var p2 = s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct;

        await Assert.That(p2).IsGreaterThanOrEqualTo(p1);
    }

    // ── ETA denominator; routing-complete makes it exact ────────────────────
    [Test]
    public async Task Eta_before_routing_uses_queued_new_bytes_not_total_minus_deduped()
    {
        // Right after scan completes: total is known, deduped lags badly, and only a little is truly
        // queued-new — total−deduped as the denominator would read multiple hours too long.
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
        await Assert.That(eta!.Value).IsBetween(8, 10);   // (totalNew 10M − 1M) / 1 MB/s ≈ 9 s
    }

    [Test]
    public async Task Routing_complete_makes_eta_exact_and_no_longer_provisional()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddQueuedNew(5_000_000);             // queued-so-far underestimates the real new-byte total
        s.SampleForEta(t0);
        s.AddUploaded(Chunk('3'), stored: 0, original: 1_000_000);   // 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        // Before routing completes the estimate is provisional — and, as the 4 s → 19 s jump below shows,
        // it is NOT an upper bound: `totalNew` only counts what routing has discovered so far.
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds!.Value).IsBetween(3, 5);
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsProvisional).IsTrue();

        s.SetNewByteTotal(20_000_000);         // routing done: the exact new-byte total is 20 MB
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(18, 20);   // (20M − 1M) / 1 MB/s ≈ 19 s
        await Assert.That(snap.EtaIsProvisional).IsFalse();
    }

    // ── Throughput is the binding term's rate, never the stale hash rate ────
    [Test]
    public async Task Throughput_at_upload_completion_is_transfer_rate_not_stale_hash_rate()
    {
        // A huge instantaneous jump inflates the hash EMA, which never decays; once the upload is done
        // (eta == 0) the reported rate must still be the transfer rate.
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
