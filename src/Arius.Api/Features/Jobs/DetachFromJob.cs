using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Jobs;

public sealed class DetachFromJob(IHubContext<JobsHub> hub) : IHubHandler
{
    public Task HandleAsync(string connectionId, string jobId) => hub.Groups.RemoveFromGroupAsync(connectionId, jobId);
}
