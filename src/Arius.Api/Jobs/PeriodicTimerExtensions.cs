namespace Arius.Api.Jobs;

/// <summary>Shared background-loop helper: await the next tick, returning <c>false</c> (rather than throwing)
/// when the host is stopping. Used by every <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> loop.</summary>
public static class PeriodicTimerExtensions
{
    public static async Task<bool> SafeWaitForNextTickAsync(this PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
