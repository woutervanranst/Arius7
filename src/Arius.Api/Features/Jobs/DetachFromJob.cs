using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>DetachFromJob</c> — leaves the job's SignalR group (the client stopped watching it).</summary>
internal static class DetachFromJob
{
    public static Task HandleAsync(Hub hub, string jobId) => hub.Groups.RemoveFromGroupAsync(hub.Context.ConnectionId, jobId);
}
