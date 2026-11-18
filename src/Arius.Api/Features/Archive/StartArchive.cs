using Arius.Api.AppData;
using Arius.Api.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>StartArchive</c> — starts an archive; the caller's connection joins the job group before events flow.</summary>
internal static class StartArchive
{
    public static async Task<string> HandleAsync(Hub hub, AppDatabase database, JobRunner jobRunner, long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await hub.Groups.AddToGroupAsync(hub.Context.ConnectionId, jobId);
        _ = jobRunner.RunArchiveAsync(repositoryId, jobId, tier, removeLocal, writePointers, fastHash);
        return jobId;
    }
}
