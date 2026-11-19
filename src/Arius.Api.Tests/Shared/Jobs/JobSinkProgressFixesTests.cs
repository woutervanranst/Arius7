using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Shared.Jobs;

/// <summary>
/// The archive progress contract: pct is the fraction (uploaded + deduped) / total and never decreases; the ETA is
/// driven by the queued-new bytes and becomes exact once routing completes; the reported throughput is the rate of
/// the binding term, never a stale hash rate.
/// </summary>
public class JobSinkProgressFixesTests
{
    private static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

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

        s.AddQueuedNew(300_000_000);   // totalNew grows from 200 MB to 500 MB
        var p2 = s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct;

        await Assert.That(p2).IsGreaterThanOrEqualTo(p1);
    }

    [Test]
    public async Task Eta_before_routing_uses_queued_new_bytes_not_total_minus_deduped()
    {
        // Just after the scan, deduped lags far behind and little is queued as new: total − deduped as the
        // denominator would overstate the ETA by hours.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddDeduped(original: 600_000_000);   // total − deduped = 400 MB
        s.AddQueuedNew(10_000_000);            // only 10 MB queued as new
        s.AddHashed(1_000_000_000);            // hashing complete, so the hash term contributes nothing

        s.SampleForEta(t0);
        s.AddUploaded(Chunk('2'), stored: 0, original: 1_000_000);   // 1 MB over 1 s = 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        var eta = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;
        await Assert.That(eta!.Value).IsBetween(8, 10);   // (10 MB − 1 MB) / 1 MB/s = 9 s
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

        // Before routing completes the ETA is provisional and not an upper bound: totalNew only counts the
        // bytes routing has discovered so far.
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds!.Value).IsBetween(3, 5);
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsProvisional).IsTrue();

        s.SetNewByteTotal(20_000_000);         // routing complete: the exact new-byte total is 20 MB
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(18, 20);   // (20 MB − 1 MB) / 1 MB/s = 19 s
        await Assert.That(snap.EtaIsProvisional).IsFalse();
    }

    [Test]
    public async Task Throughput_during_scan_reports_the_hash_rate_not_zero()
    {
        // The scan is still running (total is 0) while files are hashed. On a large, dedup-heavy repository
        // this lasts minutes with nothing uploaded, so the hash rate must be reported.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        s.SampleForEta(t0);
        s.AddHashed(20_000_000);              // 20 MB hashed over 1 s = 20 MB/s hash rate
        s.SampleForEta(t0.AddSeconds(1));

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.TotalBytes).IsEqualTo(0L);
        await Assert.That(snap.EtaSeconds).IsNull();                                         // no ETA until the scan completes
        await Assert.That(snap.HashThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000);
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000);
    }

    [Test]
    public async Task Throughput_reports_the_upload_rate_while_hashing_is_done_and_upload_runs()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // hashing complete, with a high hash rate
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(100_000_000);
        s.AddUploaded(Chunk('5'), stored: 0, original: 10_000_000);   // 10 MB over 1 s = 10 MB/s, 90 MB remain
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);                     // hashing is done
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000);
    }

    [Test]
    public async Task Throughput_is_zero_once_both_streams_are_done()
    {
        // A large hash jump inflates the hash EMA, which never decays. Once hashing reaches the total and the
        // upload has drained, both rates must read 0, not the stale hash rate.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // 1 GB in 1 s: a 1 GB/s hash EMA, and hashing is complete
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(10_000_000);
        s.AddUploaded(Chunk('4'), stored: 0, original: 10_000_000);   // all 10 MB uploaded
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.EtaSeconds).IsEqualTo(0L);                    // both terms drained
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);   // not the stale 1 GB/s
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0);
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
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(900_000, 1_100_000);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(900_000, 1_100_000);
    }
}
