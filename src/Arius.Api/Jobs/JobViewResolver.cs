using System.Text.Json;
using Arius.Api.Contracts;

namespace Arius.Api.Jobs;

/// <summary>A job's current view resolved from the single source of truth: the live <see cref="JobSink"/> if the
/// run is executing, else the persisted <c>state_json</c>, else empty. <see cref="Snapshot"/> is null only when
/// neither source exists.</summary>
public sealed record ResolvedJobView(JobSnapshot? Snapshot, CostEstimateDto? Cost, ResumeInfo? Resume, int WarningCount, IReadOnlyList<string> Warnings);

/// <summary>The one place that resolves live-sink-vs-persisted job state, shared by <c>JobsHub.AttachToJob</c> and
/// the <c>GET /jobs/{id}</c> + <c>GET /jobs/{id}/warnings</c> endpoints — previously copy-pasted three ways
/// (review #15), which let the SignalR and REST views drift for the same job.</summary>
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
            catch (JsonException) { /* fall through to empty */ }
        }

        return new ResolvedJobView(Snapshot: null, Cost: null, Resume: null, WarningCount: 0, Warnings: []);
    }
}
