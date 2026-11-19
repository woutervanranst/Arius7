namespace Arius.Api.Features.Jobs;

/// <summary>The resume state of a parked restore, for a reattaching client: whether auto-resume is on, the
/// rehydration priority ("Standard" or "High"), when rehydration started and how long it is expected to take, and
/// when the restore last ran (the initial run or the latest re-drive).</summary>
public sealed record ResumeInfo(bool AutoResume, string Priority, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours, System.DateTimeOffset LastCheckedAt)
{
    public static ResumeInfo? From(Arius.Api.Shared.Jobs.RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.Priority, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours, r.LastRunAt);
}
