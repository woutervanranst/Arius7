using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Jobs;

/// <summary>Hub <c>DetachFromJob</c> — leaves the job's SignalR group (the client stopped watching it).</summary>
public sealed class DetachFromJob(IHubContext<JobsHub> hub) : IHubHandler
{
    public Task HandleAsync(string connectionId, string jobId) => hub.Groups.RemoveFromGroupAsync(connectionId, jobId);
}
