using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Restore;

/// <summary>Starts a restore job. An empty <c>targetPaths</c> restores the whole repository.</summary>
public sealed class StartRestore(IHubContext<JobsHub> hub, AppDatabase database, JobRunner jobRunner) : IHubHandler
{
    public async Task<string> HandleAsync(string connectionId, long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await hub.Groups.AddToGroupAsync(connectionId, jobId);
        _ = jobRunner.RunRestoreAsync(repositoryId, jobId, connectionId, version, targetPaths ?? [], overwrite, noPointers);
        return jobId;
    }
}
