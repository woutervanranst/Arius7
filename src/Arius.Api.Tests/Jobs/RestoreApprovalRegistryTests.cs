using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class RestoreApprovalRegistryTests
{
    [Test]
    public async Task Resolve_completes_the_wait_with_the_chosen_priority()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", RehydratePriority.High);
        var result = await waiting;

        result.Approved.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.Priority.ShouldBe(RehydratePriority.High);
    }

    [Test]
    public async Task Resolve_null_is_a_decline()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", null);
        var result = await waiting;

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeFalse();
    }

    [Test]
    public async Task Unanswered_wait_times_out()
    {
        var reg = new RestoreApprovalRegistry();
        var result = await reg.RegisterAsync("j1", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeTrue();
    }
}
