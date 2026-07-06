using Arius.Api.Testing;

namespace Arius.Api.Integration.Tests;

public class ScenarioGateTests
{
    [Test]
    public async Task Release_before_wait_is_remembered()
    {
        var gate = new ScenarioGate();

        gate.Release(repositoryId: 1);                       // release arrives first (no waiter yet)
        var wait = gate.WaitForRelease(1, CancellationToken.None);

        await wait.WaitAsync(TimeSpan.FromSeconds(1));        // must complete promptly, not hang
        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Wait_before_release_still_completes()
    {
        var gate = new ScenarioGate();

        var wait = gate.WaitForRelease(2, CancellationToken.None);
        await Assert.That(wait.IsCompleted).IsFalse();        // still gated

        gate.Release(2);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task ReleaseAll_completes_outstanding_waiters()
    {
        var gate = new ScenarioGate();
        var a = gate.WaitForRelease(3, CancellationToken.None);
        var b = gate.WaitForRelease(4, CancellationToken.None);

        gate.ReleaseAll();

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task WaitForRelease_honours_cancellation_when_never_released()
    {
        var gate = new ScenarioGate();
        using var cts = new CancellationTokenSource();
        var wait = gate.WaitForRelease(5, cts.Token);

        cts.Cancel();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}
