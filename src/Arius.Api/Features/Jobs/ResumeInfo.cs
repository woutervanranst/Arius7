namespace Arius.Api.Features.Jobs;

/// <summary>The resume state of a restore that waits for rehydration, for a reattaching client.</summary>
/// <param name="AutoResume">Whether the restore continues automatically once the chunks are rehydrated.</param>
/// <param name="Priority">Rehydration priority: <c>Standard</c> or <c>High</c>.</param>
/// <param name="RehydrationStartedAt">When rehydration was requested.</param>
/// <param name="RehydrationWindowHours">How long rehydration is expected to take at this priority, in hours.</param>
/// <param name="LastCheckedAt">When the restore last ran: the initial run or the latest retry.</param>
public sealed record ResumeInfo(bool AutoResume, string Priority, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours, System.DateTimeOffset LastCheckedAt)
{
    public static ResumeInfo? From(Arius.Api.Shared.Jobs.RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.Priority, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours, r.LastRunAt);
}
