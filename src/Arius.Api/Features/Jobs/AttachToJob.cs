using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Jobs;

/// <summary>Joins the job's SignalR group and returns the job's current state; progress updates follow over the
/// group.</summary>
public sealed class AttachToJob(IHubContext<JobsHub> hub, AppDatabase database, JobStateRegistry jobStates) : IHubHandler
{
    public async Task<JobAttachState?> HandleAsync(string connectionId, string jobId)
    {
        await hub.Groups.AddToGroupAsync(connectionId, jobId);

        var job = database.GetJob(jobId);
        if (job is null) return null;

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
