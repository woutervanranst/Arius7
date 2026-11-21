using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Archive;

/// <summary>Hub <c>StartArchive</c>: starts an archive job. The caller joins the job's group before the job starts,
/// so it receives every event.</summary>
public sealed class StartArchive(IHubContext<JobsHub> hub, AppDatabase database, JobRunner jobRunner) : IHubHandler
{
    public async Task<string> HandleAsync(string connectionId, long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await hub.Groups.AddToGroupAsync(connectionId, jobId);
        _ = jobRunner.RunArchiveAsync(repositoryId, jobId, tier, removeLocal, writePointers, fastHash);
        return jobId;
    }
}
