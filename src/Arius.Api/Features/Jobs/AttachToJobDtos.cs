namespace Arius.Api.Contracts;

/// <summary>Snapshot-on-attach payload: the job's current status, its absolute progress snapshot,
/// the cost modal if it is awaiting-cost, and the live warning count. One round trip, one client apply-path.</summary>
public sealed record JobAttachState(string Status, Arius.Api.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount, ResumeInfo? Resume);
