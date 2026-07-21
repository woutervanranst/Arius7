using Arius.Api.Jobs;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.Logging;

namespace Arius.Api.Tests.Jobs;

public class JobSinkEtaTests
{
    // ── [ETA] diagnostics logging ────────────────────────────────────────────
    // Regression guard for the progress-header trace that ships behind ARIUS_LOG_LEVEL=Debug.
    // It was added as temporary Information-level instrumentation (5d72b83d), removed as "temp"
    // (e94670af), and re-added at Debug (7b25e92a) — but wired to the API host logger, so it never
    // reached the per-repository arius-{date}.txt file. RepositoryProviderRegistry.BuildAsync now
    // attaches the per-repo logger via AttachDiagnosticsLogger; these lock the contract in:
    //   • the tick emits an [ETA] line at Debug (not removed, not Information),
    //   • nothing is emitted when Debug is disabled, and
    //   • a logger attached AFTER construction (as BuildAsync does) starts producing lines.

    [Test]
    public async Task Eta_diagnostics_emit_an_eta_line_at_debug()
    {
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Debug);
        var s   = new JobSink("job-1", hub: null, logger: log);
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(t0);

        await Assert.That(log.Entries).Count().IsEqualTo(1);
        await Assert.That(log.Entries[0].Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(log.Entries[0].Message).StartsWith("[ETA]");
        await Assert.That(log.Entries[0].Message).Contains("job=job-1");
    }

    [Test]
    public async Task Eta_diagnostics_are_suppressed_when_debug_is_disabled()
    {
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Information);   // Debug off
        var s   = new JobSink("job-1", hub: null, logger: log);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(t0);

        await Assert.That(log.Entries).IsEmpty();
    }

    [Test]
    public async Task AttachDiagnosticsLogger_starts_diagnostics_for_a_sink_built_without_one()
    {
        // Mirrors the wiring: JobRunner builds the sink with no logger, then
        // RepositoryProviderRegistry.BuildAsync attaches the per-repo logger.
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Debug);
        var s   = new JobSink("job-1", hub: null);   // no logger yet → no output
        s.SampleForEta(t0);
        s.LogEtaDiagnostics(t0);
        await Assert.That(log.Entries).IsEmpty();

        s.AttachDiagnosticsLogger(log);
        s.LogEtaDiagnostics(t0);

        await Assert.That(log.Entries).Count().IsEqualTo(1);
        await Assert.That(log.Entries[0].Message).StartsWith("[ETA]");
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
    public async Task Eta_is_upper_bound_until_hashing_completes()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);

        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('b', 64)), 0, 1_000_000);
        s.AddHashed(4_000_000);                                   // 4 MB of 10 MB hashed
        s.SampleForEta(t0.AddSeconds(1));
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsUpperBound).IsTrue();

        s.AddHashed(6_000_000);                                   // hashing now complete (10 MB = total)
        s.SampleForEta(t0.AddSeconds(2));
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(2)).EtaIsUpperBound).IsFalse();
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

    /// <summary>Minimal in-memory <see cref="ILogger"/> that records rendered messages at/above a level,
    /// so a test can assert the [ETA] diagnostic fires (and at Debug).</summary>
    private sealed class ListLogger(LogLevel minimum) : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
