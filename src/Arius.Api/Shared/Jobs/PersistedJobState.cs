namespace Arius.Api.Shared.Jobs;

/// <summary>The <c>jobs.state_json</c> column: the last progress snapshot, the warnings tail and, for a restore, what
/// is needed to resume a parked job under the same job id. Written at each lifecycle transition (awaiting-cost,
/// rehydrating, completion) and read whenever the job has no live run.</summary>
public sealed record PersistedJobState
{
    public required JobSnapshot               Snapshot { get; init; }
    public required IReadOnlyList<string>      Warnings { get; init; }
    public          RestoreResumeState?        Resume   { get; init; }
    /// <summary>The cost estimate shown in the cost prompt, persisted so a client reattaching to an
    /// <c>awaiting-cost</c> job can show it again. <c>null</c> when the job has no cost prompt.</summary>
    public          Arius.Api.Shared.Jobs.CostEstimateDto? Cost { get; init; }
}

/// <summary>The parameters needed to re-run a restore as originally approved, without a cost prompt or a client
/// connection. <see cref="RehydrationWindow"/> is the approved priority's rehydration SLA from the cost estimate;
/// the expected ready time is <see cref="RehydrationStartedAt"/> plus that window.</summary>
public sealed record RestoreResumeState
{
    public          string?                    Version                    { get; init; }
    public required IReadOnlyList<string>      TargetPaths                { get; init; }
    public required string                     Destination                { get; init; }
    public          bool                       Overwrite                  { get; init; }
    public          bool                       NoPointers                 { get; init; }
    public required string                     Priority                   { get; init; }   // "Standard" | "High"
    public          bool                       AutoResume                 { get; init; } = true;
    public required DateTimeOffset             RehydrationStartedAt       { get; init; }
    public required DateTimeOffset             LastRunAt                  { get; init; }
    public required TimeSpan                    RehydrationWindow          { get; init; }
    /// <summary>Chunks that became ready through rehydration (<c>RehydrationStatusEvent.Rehydrated</c>). Once it is
    /// non-zero the poller checks more often. Chunks that were online from the start are excluded, so they do not
    /// end the poller's quiet window.</summary>
    public          int                        RehydratedCount { get; init; }
}
