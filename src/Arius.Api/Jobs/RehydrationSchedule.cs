namespace Arius.Api.Jobs;

/// <summary>Adaptive rehydration re-drive cadence (design §7). Pure, clock-injected so it is unit-testable.
/// High: every 15 min from start. Standard: quiet for the first ~10 h (rehydration can't finish sooner), then
/// hourly. Once a re-run has seen ≥1 chunk become available, tighten to every 15 min regardless of priority.</summary>
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

        // Standard, no chunk seen yet: one quiet window from start, then hourly re-checks.
        var elapsed = now - startedAt;
        return elapsed < Standard1 ? sinceLast >= Standard1 : sinceLast >= StandardN;
    }
}
