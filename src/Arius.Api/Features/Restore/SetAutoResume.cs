using System.Text.Json;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Turns auto-resume on or off for a rehydrating restore. When off, the poller stops re-driving the job and
/// it stays <c>rehydrating</c> until resumed manually; turning it on re-drives the job immediately.</summary>
public sealed class SetAutoResume(AppDatabase database, JobRunner jobRunner) : IHubHandler
{
    public Task HandleAsync(string jobId, bool autoResume)
    {
        var job = database.GetJob(jobId);
        if (job?.StateJson is null) return Task.CompletedTask;
        PersistedJobState? persisted;
        try { persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
        catch (JsonException) { return Task.CompletedTask; }
        if (persisted?.Resume is null) return Task.CompletedTask;
        var updated = persisted with { Resume = persisted.Resume with { AutoResume = autoResume } };
        database.SaveJobState(jobId, JsonSerializer.Serialize(updated));
        if (autoResume) _ = jobRunner.ResumeRestoreAsync(jobId);
        return Task.CompletedTask;
    }
}
