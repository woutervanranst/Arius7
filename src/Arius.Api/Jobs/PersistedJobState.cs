namespace Arius.Api.Jobs;

/// <summary>The serialized shape of the <c>jobs.state_json</c> column: the last-known progress snapshot, the
/// verbatim warnings tail, and (restore only) the parameters needed to re-drive a parked/rehydrating job with
/// the SAME jobId. Written at each lifecycle transition (awaiting-cost, rehydrating, completion); read by
/// <c>GET /jobs/{id}</c>, snapshot-on-attach for a non-live job, and the rehydration poller.</summary>
public sealed record PersistedJobState
{
    public required JobSnapshot               Snapshot { get; init; }
    public required IReadOnlyList<string>      Warnings { get; init; }
    public          RestoreResumeState?        Resume   { get; init; }
    /// <summary>The cost estimate shown at the modal, persisted so "Review cost ›" can re-render it after the
    /// live one-shot CostEstimate message is gone (reattach to an awaiting-cost job). Null for non-cost jobs.</summary>
    public          Arius.Api.Contracts.CostEstimateDto? Cost { get; init; }
}

/// <summary>Everything the poller / approval fallback needs to re-run a restore with the original intent — no
/// prompt, no re-charge, no client connection. The window is the chosen priority's rehydration
/// SLA captured from the cost estimate at approval time; "≈ hydrated by" = <see cref="RehydrationStartedAt"/> +
/// <see cref="RehydrationWindow"/>.</summary>
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
    /// <summary>Count of chunks that have become ready via rehydration (the RehydrationStatusEvent.Rehydrated
    /// bucket). &gt; 0 means "a chunk newly hydrated" → tighten the poller cadence. NOT available+rehydrated:
    /// always-online chunks must not trip the quiet window.</summary>
    public          int                        RehydratedCount { get; init; }
}
