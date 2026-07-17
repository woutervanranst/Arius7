namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Polls a condition until it holds or the timeout elapses — the shared spin-wait used across the
/// scenario integration tests. 50 ms cadence, throws on timeout.</summary>
public static class ScenarioWait
{
    public static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
