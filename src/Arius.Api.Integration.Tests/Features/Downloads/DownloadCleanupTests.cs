using System.Net.Http.Json;
using System.Net;
using Arius.Api.Features.Downloads;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Downloads;

public class DownloadCleanupTests
{
    /// <summary>A download job row in the given status (completed ones get a <c>finished_at</c> of now) plus a staged folder with one file.</summary>
    private static string AddDownload(AriusApiFactory factory, long repositoryId, string status)
    {
        var db      = factory.Services.GetRequiredService<AppDatabase>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var jobId   = Guid.NewGuid().ToString();

        var terminal = JobStatuses.Terminal.Contains(status);
        db.InsertJob(jobId, repositoryId, "download", "manual", terminal ? "running" : status);
        if (terminal)
            db.CompleteJob(jobId, status, 100, null);   // sets finished_at, which a row inserted as terminal lacks

        Stage(staging, jobId);
        return jobId;
    }

    private static void Stage(DownloadStaging staging, string jobId)
    {
        Directory.CreateDirectory(Path.Combine(staging.PathFor(jobId), "photos"));
        File.WriteAllText(Path.Combine(staging.PathFor(jobId), "photos", "a.jpg"), "12345");
    }

    /// <summary>One repository per job: a repository may only have a single active job.</summary>
    private static long AddRepository(AriusApiFactory factory, int n)
    {
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var accountId = db.ListAccounts().FirstOrDefault()?.Id ?? db.InsertAccount("acct", null);
        return db.InsertRepository($"alias-{n}", $"container-{n}", accountId, Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}"), null);
    }

    [Test]
    public async Task The_sweep_deletes_finished_downloads_after_the_period_and_keeps_newer_and_in_progress_ones()
    {
        await using var factory = new AriusApiFactory();
        var cleaner = factory.Services.GetRequiredService<DownloadCleaner>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var completed   = AddDownload(factory, AddRepository(factory, 1), "completed");
        var rehydrating = AddDownload(factory, AddRepository(factory, 2), "rehydrating");
        var orphan      = Guid.NewGuid().ToString();
        Stage(staging, orphan);

        cleaner.CleanUpExpired(DateTimeOffset.UtcNow.AddDays(6));
        await Assert.That(staging.Exists(orphan)).IsFalse();
        await Assert.That(staging.Exists(completed)).IsTrue();
        await Assert.That(staging.Exists(rehydrating)).IsTrue();

        cleaner.CleanUpExpired(DateTimeOffset.UtcNow.AddDays(8));
        await Assert.That(staging.Exists(completed)).IsFalse();
        await Assert.That(staging.Exists(rehydrating)).IsTrue();
    }

    [Test]
    public async Task A_folder_that_is_not_a_job_staging_folder_is_never_deleted_nor_counted()
    {
        await using var factory = new AriusApiFactory();
        var cleaner = factory.Services.GetRequiredService<DownloadCleaner>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var completed = AddDownload(factory, AddRepository(factory, 1), "completed");
        var foreign   = Path.Combine(Path.GetDirectoryName(staging.PathFor(completed))!, "My holiday photos");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "precious");
        using var client = factory.CreateClient();

        await Assert.That(await client.GetFromJsonAsync<DownloadUsageDto>("/api/downloads/usage")).IsEqualTo(new DownloadUsageDto(1, 5));

        cleaner.CleanUpExpired(DateTimeOffset.UtcNow.AddDays(365));
        await Assert.That(File.Exists(Path.Combine(foreign, "keep.txt"))).IsTrue();

        cleaner.CleanUpAll();
        await Assert.That(File.Exists(Path.Combine(foreign, "keep.txt"))).IsTrue();
        await Assert.That(staging.Exists(completed)).IsFalse();
    }

    [Test]
    public async Task The_sweep_does_nothing_when_clean_up_is_off()
    {
        await using var factory = new AriusApiFactory();
        var db      = factory.Services.GetRequiredService<AppDatabase>();
        var cleaner = factory.Services.GetRequiredService<DownloadCleaner>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var completed = AddDownload(factory, AddRepository(factory, 1), "completed");
        var orphan    = Guid.NewGuid().ToString();
        Stage(staging, orphan);
        db.SaveDownloadSettings(new DownloadSettings(false, 7));

        var deleted = cleaner.CleanUpExpired(DateTimeOffset.UtcNow.AddDays(365));

        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That(staging.Exists(completed)).IsTrue();
        await Assert.That(staging.Exists(orphan)).IsTrue();
    }

    [Test]
    public async Task Clean_up_all_keeps_folders_of_downloads_still_in_progress()
    {
        await using var factory = new AriusApiFactory();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var completed   = AddDownload(factory, AddRepository(factory, 1), "completed");
        var failed      = AddDownload(factory, AddRepository(factory, 2), "failed");
        var running     = AddDownload(factory, AddRepository(factory, 3), "running");
        var rehydrating = AddDownload(factory, AddRepository(factory, 4), "rehydrating");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/downloads/cleanup", content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(staging.Exists(completed)).IsFalse();
        await Assert.That(staging.Exists(failed)).IsFalse();
        await Assert.That(staging.Exists(running)).IsTrue();
        await Assert.That(staging.Exists(rehydrating)).IsTrue();
        var usage = (await response.Content.ReadFromJsonAsync<DownloadUsageDto>())!;
        await Assert.That(usage).IsEqualTo(new DownloadUsageDto(2, 10));
        await Assert.That(await client.GetFromJsonAsync<DownloadUsageDto>("/api/downloads/usage")).IsEqualTo(usage);
    }

    [Test]
    public async Task Download_settings_round_trip_and_reject_zero_days()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        await Assert.That(await client.GetFromJsonAsync<DownloadSettings>("/api/settings/downloads")).IsEqualTo(new DownloadSettings(true, 7));

        using var saved = await client.PutAsJsonAsync("/api/settings/downloads", new DownloadSettings(false, 30));
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await saved.Content.ReadFromJsonAsync<DownloadSettings>()).IsEqualTo(new DownloadSettings(false, 30));
        await Assert.That(await client.GetFromJsonAsync<DownloadSettings>("/api/settings/downloads")).IsEqualTo(new DownloadSettings(false, 30));

        foreach (var days in new[] { 0, 3651 })
        {
            using var rejected = await client.PutAsJsonAsync("/api/settings/downloads", new DownloadSettings(true, days));
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("Clean-up days must be between 1 and 3650.");
        }
        await Assert.That(await client.GetFromJsonAsync<DownloadSettings>("/api/settings/downloads")).IsEqualTo(new DownloadSettings(false, 30));
    }

    [Test]
    public async Task Deleting_a_repository_deletes_its_download_folders()
    {
        await using var factory = new AriusApiFactory();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var repoId  = AddRepository(factory, 1);
        var other   = AddRepository(factory, 2);
        var mine    = AddDownload(factory, repoId, "completed");
        var theirs  = AddDownload(factory, other, "completed");
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync($"/api/repos/{repoId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(staging.Exists(mine)).IsFalse();
        await Assert.That(staging.Exists(theirs)).IsTrue();
    }
}
