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

    // ── Throughput: two independently-honest streams, each zeroed when idle/done ────
    [Test]
    public async Task Throughput_during_scan_reports_the_hash_rate_not_zero()
    {
        // Scan still running (total == 0) while files are hashed concurrently. On a large, dedup-heavy
        // repo this window lasts many minutes with nothing uploaded yet — the hash stream must be shown.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();               // no SetTotals → total == 0 (enumeration not complete)

        s.SampleForEta(t0);                   // baseline
        s.AddHashed(20_000_000);              // 20 MB hashed over 1 s = 20 MB/s hash rate
        s.SampleForEta(t0.AddSeconds(1));

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.TotalBytes).IsEqualTo(0L);                                    // still scanning
        await Assert.That(snap.EtaSeconds).IsNull();                                         // estimating, by design
        await Assert.That(snap.HashThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000); // live hashing surfaced
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0);                  // nothing uploaded yet
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000);     // dominant = hash
    }

    [Test]
    public async Task Throughput_reports_the_upload_rate_while_hashing_is_done_and_upload_runs()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // hashing complete (hashed == total) with a big hash EMA
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(100_000_000);        // 100 MB new to upload
        s.AddUploaded(Chunk('5'), stored: 0, original: 10_000_000);   // 10 MB over 1 s = 10 MB/s, 90 MB remain
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);                     // hashing done → not shown
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000); // ~10 MB/s upload
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000);       // dominant = upload
    }

    [Test]
    public async Task Throughput_is_zero_once_both_streams_are_done()
    {
        // A huge instantaneous hash jump inflates the hash EMA (which never decays). Once hashing has
        // caught up to the total AND the upload has drained, BOTH streams read "done" (rate 0) — never
        // the stale ~1 GB/s hash rate the old tie-break surfaced.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // 1 GB "hashed" in 1 s → ~1 GB/s hash EMA; hashed == total (done)
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(10_000_000);
        s.AddUploaded(Chunk('4'), stored: 0, original: 10_000_000);   // all 10 MB uploaded (done)
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.EtaSeconds).IsEqualTo(0L);                    // both terms drained
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);   // hashing done, not ~1 GB/s
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0); // upload done
        await Assert.That(snap.ThroughputBytesPerSec).IsEqualTo(0.0);
    }

    [Test]
    public async Task Restore_throughput_reports_the_download_rate_on_the_upload_stream()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetRestoreTotals(files: 5, bytes: 10_000_000);

        s.SampleForEta(t0);
        s.ReportRestoreStreamed("f", 1_000_000);   // 1 MB downloaded over 1 s = 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);                  // no hashing on restore
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(900_000, 1_100_000); // download rate
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(900_000, 1_100_000);
    }
}
