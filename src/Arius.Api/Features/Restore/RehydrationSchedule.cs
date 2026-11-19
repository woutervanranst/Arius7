namespace Arius.Api.Features.Restore;

/// <summary>When a rehydrating restore is due to be re-driven. High priority: every 15 minutes. Standard priority:
/// not until 10 hours after the start (rehydration cannot finish sooner), then hourly. Once a chunk has become
/// available: every 15 minutes, regardless of priority.</summary>
public static class RehydrationSchedule
{
    private static readonly TimeSpan Tight     = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Standard1 = TimeSpan.FromHours(10);
    private static readonly TimeSpan StandardN = TimeSpan.FromHours(1);

    public static bool IsDue(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset lastRunAt, string priority, bool firstChunkSeen)
    {
        var sinceLast = now - lastRunAt;
        if (firstChunkSeen || priority == "High")
            return sinceLast >= Tight;

        var elapsed = now - startedAt;
        return elapsed < Standard1 ? sinceLast >= Standard1 : sinceLast >= StandardN;
    }
}
