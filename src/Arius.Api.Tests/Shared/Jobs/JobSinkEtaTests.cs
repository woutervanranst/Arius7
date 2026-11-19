using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.Logging;

namespace Arius.Api.Tests.Shared.Jobs;

public class JobSinkEtaTests
{
    [Test]
    public async Task Eta_diagnostics_emit_an_eta_line_at_debug()
    {
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Debug);
        var s   = new JobSink("job-1", hub: null, logger: log);
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(s.BuildSnapshot(t0));

        await Assert.That(log.Entries).Count().IsEqualTo(1);
        await Assert.That(log.Entries[0].Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(log.Entries[0].Message).StartsWith("[ETA]");
        await Assert.That(log.Entries[0].Message).Contains("job=job-1");
    }

    [Test]
    public async Task Eta_diagnostics_line_mirrors_the_snapshot_wire_fields()
    {
        // The [ETA] line is the raw snapshot the web receives, so every field the client reads must be in it.
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Debug);
        var s   = new JobSink("job-1", hub: null, logger: log);
        s.SetPhase("upload");
        s.SetStatus("running");
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(4_000_000);
        s.AddScanned(10_000_000);
        s.AddHashed(6_000_000);
        s.AddDeduped(2_000_000);
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), stored: 0, original: 1_000_000);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(s.BuildSnapshot(t0));
        var msg = log.Entries[0].Message;

        foreach (var token in new[]
                 {
                     "phase=upload", "status=running", "pct=", "eta=", "provisional=", "tp=", "hashTp=", "upTp=", "warnings=",
                     "total=10000000", "totalNew=4000000", "scanned=10000000/1f", "hashed=6000000",
                     "uploaded=1000000", "deduped=2000000/1f", "chunksTotal=", "avail=", "pending=",
                 })
            await Assert.That(msg).Contains(token);
    }

    [Test]
    public async Task Eta_diagnostics_are_suppressed_when_debug_is_disabled()
    {
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Information);   // Debug off
        var s   = new JobSink("job-1", hub: null, logger: log);
        s.SampleForEta(t0);

        s.LogEtaDiagnostics(s.BuildSnapshot(t0));

        await Assert.That(log.Entries).IsEmpty();
    }

    [Test]
    public async Task AttachDiagnosticsLogger_starts_diagnostics_for_a_sink_built_without_one()
    {
        // JobRunner builds the sink without a logger; the registry attaches one later.
        var t0  = DateTimeOffset.UnixEpoch;
        var log = new ListLogger(LogLevel.Debug);
        var s   = new JobSink("job-1", hub: null);
        s.SampleForEta(t0);
        s.LogEtaDiagnostics(s.BuildSnapshot(t0));
        await Assert.That(log.Entries).IsEmpty();

        s.AttachDiagnosticsLogger(log);
        s.LogEtaDiagnostics(s.BuildSnapshot(t0));

        await Assert.That(log.Entries).Count().IsEqualTo(1);
        await Assert.That(log.Entries[0].Message).StartsWith("[ETA]");
    }

    [Test]
    public async Task Eta_is_null_until_total_new_bytes_known_then_uses_windowed_rate()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        // No totals yet, so no ETA. Each upload uses a distinct chunk hash so that it is counted.
        s.AddUploaded(ChunkHash.Parse(new string('1', 64)), 0, 1_000);
        s.SampleForEta(t0);
        await Assert.That(s.BuildSnapshot(t0).EtaSeconds).IsNull();

        // With totals known: 1 MB uploaded in 1 s is 1 MB/s, and 9 MB remain, so about 9 s.
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);
        s.SampleForEta(t0);                    // warm start: 1 000 bytes already uploaded
        s.AddUploaded(ChunkHash.Parse(new string('2', 64)), 0, 1_000_000);
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

        // Warm start already past the total, as when a re-baseline shrinks totalNew below the bytes
        // already uploaded.
        s.AddUploaded(ChunkHash.Parse(new string('3', 64)), 0, 5_000);
        s.SetTotals(files: 1, bytes: 1_000);
        s.AddQueuedNew(1_000);
        s.SampleForEta(t0);

        s.AddUploaded(ChunkHash.Parse(new string('4', 64)), 0, 2_000);   // uploaded now far exceeds totalNew
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

        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), 0, 1_000_000);
        s.SampleForEta(t0.AddSeconds(1));                                      // 1 MB/s
        var eta1 = s.BuildSnapshot(t0.AddSeconds(1)).EtaSeconds;

        // A tick without progress holds the rate, so with the remaining bytes unchanged the ETA is unchanged.
        s.SampleForEta(t0.AddSeconds(2));
        var eta2 = s.BuildSnapshot(t0.AddSeconds(2)).EtaSeconds;

        await Assert.That(eta1).IsNotNull();
        await Assert.That(eta2).IsEqualTo(eta1);
    }

    [Test]
    public async Task Eta_is_hash_bound_when_there_is_nothing_to_upload()
    {
        // Fully deduplicated archive: 100 MB to hash and nothing to upload, so the ETA is the remaining hash time.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 100, bytes: 100_000_000);
        s.AddQueuedNew(0);
        s.AddDeduped(100_000_000);

        s.SampleForEta(t0);
        s.AddHashed(10_000_000);
        s.SampleForEta(t0.AddSeconds(1));  // 10 MB/s hash rate with 90 MB remaining: about 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_500_000, 10_500_000);  // the hash rate
        await Assert.That(snap.EtaIsProvisional).IsTrue();                                 // hashing not done
    }

    [Test]
    public async Task Eta_is_provisional_until_routing_completes()
    {
        // Routing completion, not hashing, ends the provisional state: skipped or unreadable files mean hashed
        // may never reach the total.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);

        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('b', 64)), 0, 1_000_000);
        s.AddHashed(10_000_000);                                  // hashing complete
        s.SampleForEta(t0.AddSeconds(1));
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsProvisional).IsTrue();   // routing not complete

        s.SetNewByteTotal(10_000_000);                            // routing complete: exact total known
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsProvisional).IsFalse();
    }

    [Test]
    public async Task Restore_eta_uses_download_rate_and_is_never_provisional()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetRestoreTotals(files: 5, bytes: 10_000_000);

        s.SampleForEta(t0);
        s.ReportRestoreStreamed("f", 1_000_000);
        s.SampleForEta(t0.AddSeconds(1));                         // 1 MB/s with 9 MB remaining: about 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.EtaIsProvisional).IsFalse();
    }

    [Test]
    public async Task Adaptive_window_dampens_a_late_spike_more_than_an_early_one()
    {
        // Both sinks get the same 1 MB/s warm-up and 3 MB spike. On one the spike lands late, when the window is
        // wide and the smoothing factor small, so the rate barely moves; on the other it lands early.
        static ChunkHash Chunk(int i) => ChunkHash.Parse(i.ToString("x").PadLeft(64, '0'));
        static double RateAfterSpike(int warmupTicks)
        {
            var t0 = DateTimeOffset.UnixEpoch;
            var s  = new JobSink();
            s.SetTotals(1, 1_000_000_000);
            s.AddQueuedNew(1_000_000_000);
            var t = t0;
            s.SampleForEta(t);                                   // anchors the elapsed time
            for (var i = 0; i < warmupTicks; i++)
            {
                s.AddUploaded(Chunk(i), 0, 1_000_000);           // steady 1 MB/s
                t = t.AddSeconds(1);
                s.SampleForEta(t);
            }
            s.AddUploaded(Chunk(10_000), 0, 3_000_000);          // 3 MB spike
            t = t.AddSeconds(1);
            s.SampleForEta(t);
            return s.BuildSnapshot(t).ThroughputBytesPerSec;     // upload is the binding term, so this is the upload rate
        }

        var early = RateAfterSpike(warmupTicks: 1);              // elapsed about 2 s: window 3 s
        var late  = RateAfterSpike(warmupTicks: 120);            // elapsed about 121 s: window about 12 s
        await Assert.That(early).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsLessThan(early);               // a wider window smooths more
    }

    /// <summary>An in-memory <see cref="ILogger"/> that records rendered messages at or above a minimum level.</summary>
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
