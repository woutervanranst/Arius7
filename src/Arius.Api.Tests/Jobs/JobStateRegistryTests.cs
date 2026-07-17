using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public class JobStateRegistryTests
{
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

    [Test]
    public async Task CancelLive_returns_false_without_throwing_when_the_cts_was_already_disposed()
    {
        var reg  = new JobStateRegistry();
        var sink = new JobSink();
        reg.Register("job-1", sink);
        sink.Cts.Dispose();                      // simulate the job's finally having run

        reg.CancelLive("job-1").ShouldBeFalse(); // no ObjectDisposedException escapes
    }
}
