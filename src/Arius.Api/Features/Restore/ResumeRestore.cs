using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Hub <c>ResumeRestore</c> — manual "Restore now" for a rehydrating restore whose auto-resume is off.</summary>
public sealed class ResumeRestore(JobRunner jobRunner) : IHubHandler
{
    public Task HandleAsync(string jobId)
    {
        _ = jobRunner.ResumeRestoreAsync(jobId);
        return Task.CompletedTask;
    }
}
