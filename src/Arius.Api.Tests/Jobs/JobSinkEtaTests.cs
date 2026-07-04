using Arius.Api.Jobs;

namespace Arius.Api.Tests.Jobs;

public class JobSinkEtaTests
{
    [Test]
    public async Task Eta_is_null_until_total_new_bytes_known_then_uses_windowed_rate()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        // No totals yet → estimating (null).
        s.AddUploaded(0, 1_000);
        s.SampleForEta(t0);
        await Assert.That(s.BuildSnapshot(t0).EtaSeconds).IsNull();

        // Totals known; 1 MB uploaded over 1 s = 1 MB/s; 9 MB remaining → 9 s.
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.SampleForEta(t0);                    // 1_000 @ t0  (warm start)
        s.AddUploaded(0, 1_000_000);           // now 1_001_000 uploaded
        s.SampleForEta(t0.AddSeconds(1));
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
    }
}
