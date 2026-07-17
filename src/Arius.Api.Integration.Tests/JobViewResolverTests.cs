using Arius.Api.Contracts;
using Arius.Api.Jobs;

namespace Arius.Api.Integration.Tests;

public class JobViewResolverTests
{
    [Test]
    public async Task Resolve_prefers_the_live_sink()
    {
        var jobStates = new JobStateRegistry();
        var sink = new JobSink("job-1", null!);
        sink.SetRestoreTotals(3, 3000);
        jobStates.Register("job-1", sink);

        var view = JobViewResolver.Resolve(jobStates, "job-1", stateJson: null);

        await Assert.That(view.Snapshot).IsNotNull();
        await Assert.That(view.Snapshot!.RestoreTotalFiles).IsEqualTo(3L);
    }

    [Test]
    public async Task Resolve_falls_back_to_persisted_state_json()
    {
        var jobStates = new JobStateRegistry();   // nothing registered
        var persisted = new PersistedJobState
        {
            Snapshot = new JobSink("job-2", null!).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = ["boom"],
            Resume = null,
            Cost = null,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(persisted);

        var view = JobViewResolver.Resolve(jobStates, "job-2", json);

        await Assert.That(view.Warnings.Count).IsEqualTo(1);
        await Assert.That(view.Snapshot).IsNotNull();
    }

    [Test]
    public async Task Resolve_returns_an_empty_view_when_nothing_is_available()
    {
        var view = JobViewResolver.Resolve(new JobStateRegistry(), "missing", stateJson: null);
        await Assert.That(view.Snapshot).IsNull();
        await Assert.That(view.Cost).IsNull();
        await Assert.That(view.WarningCount).IsEqualTo(0);
        await Assert.That(view.Warnings.Count).IsEqualTo(0);
    }
}
