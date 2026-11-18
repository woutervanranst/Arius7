using Arius.Api.AppData;
using Arius.Api.Contracts;
using Arius.Api.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>AttachToJob</c> — joins the job's SignalR group and returns its current state: live from the
/// registry if the job is running, else reconstructed from persisted state_json for a parked/finished job.
/// Progress deltas follow.</summary>
internal static class AttachToJob
{
    public static async Task<JobAttachState?> HandleAsync(Hub hub, AppDatabase database, JobStateRegistry jobStates, string jobId)
    {
        await hub.Groups.AddToGroupAsync(hub.Context.ConnectionId, jobId);

        var job = database.GetJob(jobId);
        if (job is null) return null;

        // A job blocked in the ConfirmRehydration callback (genuinely parked at awaiting-cost, still within the
        // approval window) still has a LIVE sink here — JobRunner's method has not returned, so nothing has
        // removed it from jobStates yet. JobViewResolver reads the cost/resume the run staged on the sink for
        // exactly this case rather than hardcoding null.
        var view = JobViewResolver.Resolve(jobStates, jobId, job.StateJson);
        return new JobAttachState(job.Status, view.Snapshot ?? EmptySnapshot(jobId), view.Cost, view.WarningCount, view.Resume);
    }

    private static JobSnapshot EmptySnapshot(string jobId) => new()
    {
        JobId = jobId, Phase = "unknown",
        TotalBytes = 0, TotalNewBytes = 0, ScannedBytes = 0, ScannedFiles = 0, HashedBytes = 0, UploadedBytes = 0,
        DedupedBytes = 0, DedupedFiles = 0, EtaSeconds = null, ThroughputBytesPerSec = 0, Pct = 0,
        Stats = new Dictionary<string, string>(), WarningCount = 0,
        RestoreTotalFiles = 0, FilesRestored = 0, RestoreTotalBytes = 0, BytesRestored = 0,
        ChunksAvailable = 0, ChunksRehydrated = 0, ChunksNeedingRehydration = 0, ChunksPending = 0,
        ChunksTotal = 0,
    };
}
