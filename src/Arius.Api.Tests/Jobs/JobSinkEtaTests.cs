using Arius.Api.Hubs;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.Logging;

namespace Arius.Api.Tests.Jobs;

public class JobSinkEtaTests
{
    [Test]
    public async Task Eta_diagnostics_emit_one_debug_line_once_a_logger_is_attached()
    {
        // Mirrors the wiring: JobRunner builds the sink without a logger (silent), the registry attaches one after.
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger();
        var s   = new JobSink("job-1", hub: null);
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(t0);
        await Assert.That(log.Entries).IsEmpty();

        s.AttachDiagnosticsLogger(log);
        s.LogEtaDiagnostics(t0);

        await Assert.That(log.Entries).Count().IsEqualTo(1);
        await Assert.That(log.Entries[0].Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(log.Entries[0].Message).StartsWith("[ETA]");
        await Assert.That(log.Entries[0].Message).Contains("job=job-1");
    }

    [Test]
    public async Task Eta_is_null_until_total_new_bytes_known_then_uses_windowed_rate()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        // No totals yet → estimating (null). Distinct chunk hashes so each call adds (real uploads are distinct chunks).
        s.AddUploaded(ChunkHash.Parse(new string('1', 64)), 0, 1_000);
        s.SampleForEta(t0);
        await Assert.That(s.BuildSnapshot(t0).EtaSeconds).IsNull();

        // Totals known; 1 MB uploaded over 1 s = 1 MB/s; 9 MB remaining → 9 s.
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);             // additive new-bytes-to-upload now known (no dedup here)
        s.SampleForEta(t0);                    // 1_000 @ t0  (warm start)
        s.AddUploaded(ChunkHash.Parse(new string('2', 64)), 0, 1_000_000);   // distinct chunk → now 1_001_000 uploaded
        s.SampleForEta(t0.AddSeconds(1));
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
    }

    [Test]
    public async Task Eta_is_never_negative_when_uploaded_exceeds_rebaselined_total()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        // Warm start already past the (small) total — e.g. a re-baseline shrank totalNew
        // below what's already been uploaded.
        s.AddUploaded(ChunkHash.Parse(new string('3', 64)), 0, 5_000);
        s.SetTotals(files: 1, bytes: 1_000);
        s.AddQueuedNew(1_000);                 // additive new-bytes-to-upload now known (no dedup here)
        s.SampleForEta(t0);                    // 5_000 @ t0 (warm start)

        s.AddUploaded(ChunkHash.Parse(new string('4', 64)), 0, 2_000);   // distinct chunk → now 7_000 uploaded; totalNew (1_000) is dwarfed
        s.SampleForEta(t0.AddSeconds(1));

        var eta = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;
        await Assert.That(eta is null || eta >= 0).IsTrue();
    }

    [Test]
    public async Task Eta_holds_steady_across_a_no_progress_tick()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);

        s.SampleForEta(t0);                                                    // baseline
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), 0, 1_000_000);
        s.SampleForEta(t0.AddSeconds(1));                                      // 1 MB/s established
        var eta1 = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;

        // No new bytes this tick → the rate is HELD, so the ETA must not inflate. Remaining and rate
        // unchanged ⇒ ETA unchanged.
        s.SampleForEta(t0.AddSeconds(2));
        var eta2 = s.BuildSnapshot(t0.AddSeconds(2)).EtaSeconds;

        await Assert.That(eta1).IsNotNull();
        await Assert.That(eta2).IsEqualTo(eta1);
    }

    [Test]
    public async Task Eta_is_hash_bound_when_there_is_nothing_to_upload()
    {
        // Fully-deduped archive: 100 MB to hash, 0 new bytes to upload. The OLD upload-only model
        // read ~null/0 here; the new model must surface the remaining HASH time.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 100, bytes: 100_000_000);
        s.AddQueuedNew(0);                 // nothing new queued
        s.AddDeduped(100_000_000);         // everything deduped → total-deduped = 0

        s.SampleForEta(t0);                // baseline
        s.AddHashed(10_000_000);           // 10 MB hashed
        s.SampleForEta(t0.AddSeconds(1));  // 10 MB/s hash rate; 90 MB remaining → 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_500_000, 10_500_000);  // reports the HASH rate
        await Assert.That(snap.EtaIsUpperBound).IsTrue();                                  // hashing not done
    }

    [Test]
    public async Task Eta_is_an_upper_bound_until_routing_completes()
    {
        // Routing draining is the gate, not hashing: skipped/unreadable files mean hashed may never reach
        // the total, so until routing completes the denominator is the still-growing "queued so far" sum and
        // the ETA stays a provisional upper bound ("≤"). Driven through the forwarder so its wiring is covered too.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(5_000_000);                                // queued-so-far underestimates the real total

        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('b', 64)), 0, 1_000_000);   // 1 MB over 1 s = 1 MB/s
        s.AddHashed(10_000_000);                                  // hashing complete…
        s.SampleForEta(t0.AddSeconds(1));

        var provisional = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(provisional.EtaIsUpperBound).IsTrue();             // …but routing hasn't drained
        await Assert.That(provisional.EtaSeconds!.Value).IsBetween(3, 5);    // (5M − 1M) / 1 MB/s ≈ 4 s

        await new RoutingCompleteForwarder(s).Handle(new RoutingCompleteEvent(10_000_000), default);

        var exact = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(exact.EtaIsUpperBound).IsFalse();
        await Assert.That(exact.EtaSeconds!.Value).IsBetween(8, 10);         // (10M − 1M) / 1 MB/s ≈ 9 s
    }

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
        s.AddUploaded(ChunkHash.Parse(new string('2', 64)), stored: 0, original: 1_000_000);   // 1 MB over 1 s = 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        var eta = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;
        await Assert.That(eta!.Value).IsBetween(8, 10);   // (totalNew 10M − 1M) / 1 MB/s ≈ 9 s
    }

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
        s.AddUploaded(ChunkHash.Parse(new string('4', 64)), stored: 0, original: 10_000_000);   // all 10 MB over 1 s = 10 MB/s
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.EtaSeconds).IsEqualTo(0L);                          // upload complete
        await Assert.That(snap.ThroughputBytesPerSec).IsLessThan(100_000_000.0);   // NOT the ~1 GB/s hash rate
        await Assert.That(snap.ThroughputBytesPerSec).IsGreaterThan(1_000_000.0);  // ~10 MB/s transfer
    }

    [Test]
    public async Task Restore_eta_uses_download_rate_and_is_not_an_upper_bound()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetRestoreTotals(files: 5, bytes: 10_000_000);

        s.SampleForEta(t0);
        s.ReportRestoreStreamed("f", 1_000_000);                  // 1 MB downloaded
        s.SampleForEta(t0.AddSeconds(1));                         // 1 MB/s; 9 MB remaining → 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.EtaIsUpperBound).IsFalse();
    }

    [Test]
    public async Task Adaptive_window_dampens_a_late_spike_more_than_an_early_one()
    {
        // Same 1 MB/s warm-up + same 3 MB spike; the spike lands after a long elapsed on one sink
        // (wide window → small alpha → barely moves) and after a short elapsed on the other.
        static ChunkHash Chunk(int i) => ChunkHash.Parse(i.ToString("x").PadLeft(64, '0'));
        static double RateAfterSpike(int warmupTicks)
        {
            var t0 = DateTimeOffset.UnixEpoch;
            var s  = new JobSink();
            s.SetTotals(1, 1_000_000_000);
            s.AddQueuedNew(1_000_000_000);
            var t = t0;
            s.SampleForEta(t);                                   // start (anchors elapsed)
            for (var i = 0; i < warmupTicks; i++)
            {
                s.AddUploaded(Chunk(i), 0, 1_000_000);           // steady 1 MB/s
                t = t.AddSeconds(1);
                s.SampleForEta(t);
            }
            s.AddUploaded(Chunk(10_000), 0, 3_000_000);          // 3 MB spike this tick
            t = t.AddSeconds(1);
            s.SampleForEta(t);
            return s.BuildSnapshot(t).ThroughputBytesPerSec;     // upload binds → this is the transfer rate
        }

        var early = RateAfterSpike(warmupTicks: 1);              // elapsed ≈ 2 s → τ = 3 s
        var late  = RateAfterSpike(warmupTicks: 120);            // elapsed ≈ 121 s → τ ≈ 12 s
        await Assert.That(early).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsLessThan(early);               // wider window = steadier
    }

    /// <summary>Minimal in-memory <see cref="ILogger"/> that records rendered messages and their level, so a
    /// test can assert the [ETA] diagnostic fires and at which level.</summary>
    private sealed class ListLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
