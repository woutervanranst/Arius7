using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public class JobStateRegistryTests
{
    [Test]
    public async Task Register_then_TryGet_then_Remove()
    {
        var reg  = new JobStateRegistry();
        var sink = new JobSink();
        reg.Register("job-1", sink);

        await Assert.That(reg.TryGet("job-1", out var got)).IsTrue();
        await Assert.That(got).IsSameReferenceAs(sink);
        await Assert.That(reg.ActiveJobIds).Contains("job-1");

        reg.Remove("job-1");
        await Assert.That(reg.TryGet("job-1", out _)).IsFalse();
    }

    [Test]
    public async Task CancelLive_cancels_a_registered_jobs_token_and_reports_presence()
    {
        var reg  = new JobStateRegistry();
        var sink = new JobSink();
        reg.Register("job-1", sink);

        (await Task.FromResult(reg.CancelLive("job-1"))).ShouldBeTrue();
        sink.Cts.IsCancellationRequested.ShouldBeTrue();

        reg.CancelLive("absent").ShouldBeFalse();
    }
}
