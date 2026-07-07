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
}
