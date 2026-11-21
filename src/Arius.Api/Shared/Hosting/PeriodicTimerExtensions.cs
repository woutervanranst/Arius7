namespace Arius.Api.Shared.Hosting;

/// <summary>Waits for the next tick of a background-service loop, returning <c>false</c> instead of throwing when the
/// host is stopping.</summary>
public static class PeriodicTimerExtensions
{
    public static async Task<bool> SafeWaitForNextTickAsync(this PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
