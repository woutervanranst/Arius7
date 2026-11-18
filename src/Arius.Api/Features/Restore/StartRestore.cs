using Arius.Api.AppData;
using Arius.Api.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>StartRestore</c> — starts a restore (empty targetPaths = whole repository).</summary>
internal static class StartRestore
{
    public static async Task<string> HandleAsync(Hub hub, AppDatabase database, JobRunner jobRunner, long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await hub.Groups.AddToGroupAsync(hub.Context.ConnectionId, jobId);
        _ = jobRunner.RunRestoreAsync(repositoryId, jobId, hub.Context.ConnectionId, version, targetPaths ?? [], overwrite, noPointers);
        return jobId;
    }
}
