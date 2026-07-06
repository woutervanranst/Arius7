using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Integration.Tests;

public class ApprovalRegistryTests
{
    [Test]
    public async Task RegisterAsync_returns_the_resolved_priority()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("job-1", CancellationToken.None);
        await Assert.That(reg.HasPending("job-1")).IsTrue();

        reg.Resolve("job-1", RehydratePriority.High);

        await Assert.That(await wait).IsEqualTo(RehydratePriority.High);
        await Assert.That(reg.HasPending("job-1")).IsFalse();   // entry removed after resolve
    }

    [Test]
    public async Task RegisterAsync_returns_null_when_declined()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("job-2", CancellationToken.None);
        reg.Resolve("job-2", null);
        await Assert.That(await wait).IsNull();
    }

    [Test]
    public async Task RegisterAsync_throws_when_the_token_is_cancelled()
    {
        var reg = new RestoreApprovalRegistry();
        using var cts = new CancellationTokenSource();
        var wait = reg.RegisterAsync("job-3", cts.Token);
        cts.Cancel();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}
