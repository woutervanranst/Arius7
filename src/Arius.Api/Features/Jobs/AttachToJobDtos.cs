using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary>Everything a client needs when it attaches to a job, in one round trip: the status, the progress
/// snapshot, the pending cost estimate when awaiting cost, the warning count and the restore resume state.</summary>
public sealed record JobAttachState(string Status, Arius.Api.Shared.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount, ResumeInfo? Resume);
