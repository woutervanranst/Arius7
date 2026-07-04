using Arius.Api.Jobs;

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
}
