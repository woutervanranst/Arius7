using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Hub <c>ResumeRestore</c> — manual "Restore now" for a rehydrating restore whose auto-resume is off.</summary>
internal static class ResumeRestore
{
    public static Task HandleAsync(JobRunner jobRunner, string jobId)
    {
        _ = jobRunner.ResumeRestoreAsync(jobId);
        return Task.CompletedTask;
    }
}
