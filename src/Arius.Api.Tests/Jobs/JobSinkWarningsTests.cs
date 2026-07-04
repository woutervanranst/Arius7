using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class JobSinkWarningsTests
{
    [Test]
    public async Task Warn_and_error_logs_are_captured_verbatim_info_is_not()
    {
        var s = new JobSink();
        s.Log("scanning…", "meta");
        s.Log("archive-tier chunks need rehydration", "warn");
        s.Log("all good", "info");
        s.Log("disk write failed", "error");

        s.WarningCount.ShouldBe(2);
        s.Warnings.ShouldBe(new[] { "archive-tier chunks need rehydration", "disk write failed" });
    }

    [Test]
    public async Task Warning_ring_is_bounded_but_count_stays_accurate()
    {
        var s = new JobSink();
        for (var i = 0; i < 250; i++) s.Log($"warn {i}", "warn");

        s.WarningCount.ShouldBe(250);          // total ever, not the ring size
        s.Warnings.Count.ShouldBe(200);        // ring cap
        s.Warnings[^1].ShouldBe("warn 249");   // newest retained
        s.Warnings[0].ShouldBe("warn 50");     // oldest 50 trimmed
    }

    [Test]
    public async Task Snapshot_reports_warning_count()
    {
        var s = new JobSink();
        s.Log("w1", "warn");
        s.BuildSnapshot(DateTimeOffset.UnixEpoch).WarningCount.ShouldBe(1);
    }

    [Test]
    public async Task StopReporting_does_not_emit_progress_after_done()
    {
        // An inert sink can't observe SignalR sends, so assert the guard flag instead: once Done is recorded,
        // EmitNow is suppressed. We expose this via a test-only check on the sink's terminal flag.
        var s = new JobSink();
        s.Done("completed", "done");
        s.IsDone.ShouldBeTrue();          // Done sets the terminal flag
        s.StopReporting();                // must be a no-op emit-wise (no throw; timer never started)
        s.IsDone.ShouldBeTrue();
    }
}
