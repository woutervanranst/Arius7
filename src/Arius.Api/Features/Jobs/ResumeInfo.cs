namespace Arius.Api.Features.Jobs;

/// <summary>The parked-restore resume facts a reattaching client needs: whether auto-resume is on, the rehydration
/// priority ("Standard" | "High") and SLA window ("≈ hydrated by" = RehydrationStartedAt + RehydrationWindowHours),
/// and when Azure was last checked (the initial run or the latest re-drive). Null for jobs with no restore-resume
/// state.</summary>
public sealed record ResumeInfo(bool AutoResume, string Priority, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours, System.DateTimeOffset LastCheckedAt)
{
    /// <summary>Maps the persisted restore-resume state to the wire DTO (null-safe). Shared by the hub's AttachToJob + GET /jobs/{id}.</summary>
    public static ResumeInfo? From(Arius.Api.Shared.Jobs.RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.Priority, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours, r.LastRunAt);
}
