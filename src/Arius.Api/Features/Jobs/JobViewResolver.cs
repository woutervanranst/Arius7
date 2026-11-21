using System.Text.Json;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary>A job's current state: from the live <see cref="JobSink"/> while the run executes, otherwise from the
/// persisted <c>state_json</c>. <see cref="Snapshot"/> is null when neither exists.</summary>
public sealed record ResolvedJobView(JobSnapshot? Snapshot, CostEstimateDto? Cost, ResumeInfo? Resume, int WarningCount, IReadOnlyList<string> Warnings);

/// <summary>Resolves a job's state from its live sink or its persisted state. A run blocked at the cost prompt is
/// still live, so its pending cost estimate and resume state come from the sink.</summary>
public static class JobViewResolver
{
    public static ResolvedJobView Resolve(JobStateRegistry jobStates, string jobId, string? stateJson)
    {
        if (jobStates.TryGet(jobId, out var sink))
            return new ResolvedJobView(sink.BuildSnapshot(DateTimeOffset.UtcNow), sink.PendingCost, ResumeInfo.From(sink.PendingResume), sink.WarningCount, sink.Warnings);

        if (stateJson is not null)
        {
            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedJobState>(stateJson);
                if (persisted is not null)
                    return new ResolvedJobView(persisted.Snapshot, persisted.Cost, ResumeInfo.From(persisted.Resume), persisted.Snapshot.WarningCount, persisted.Warnings ?? []);
            }
            catch (JsonException) { /* unreadable state: treat as empty */ }
        }

        return new ResolvedJobView(Snapshot: null, Cost: null, Resume: null, WarningCount: 0, Warnings: []);
    }
}
