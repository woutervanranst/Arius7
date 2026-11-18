using System.Text.Json;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Hub <c>SetAutoResume</c> — toggles auto-resume for a rehydrating restore. OFF stops the poller
/// re-driving it (status stays rehydrating; the UI shows "≈ hydrated by" + a manual Restore-now). ON re-drives
/// immediately.</summary>
internal static class SetAutoResume
{
    public static Task HandleAsync(AppDatabase database, JobRunner jobRunner, string jobId, bool autoResume)
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
