using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Downloads;
using Arius.Api.FakeTestHost;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class DownloadEndpointsTests
{
    private static readonly DownloadFile[] Collected = [new("photos/a.jpg", 1000), new("docs/b.txt", 500)];

    private static async Task<(long RepoId, string JobId, Task Run)> StartDownloadAsync(AriusApiFactory factory, bool gated = false, bool awaitRun = true)
    {
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.OnlineDownload(gated));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunDownloadAsync(repoId, jobId, "v1", Collected);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.Standard);
        if (awaitRun) await run;
        return (repoId, jobId, run);
    }

    [Test]
    public async Task State_lists_each_file_with_ready_and_downloaded_and_the_clean_up_date()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, _) = await StartDownloadAsync(factory);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.MarkDownloadFilesDownloaded(jobId, ["photos/a.jpg"]);
        using var client = factory.CreateClient();

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/download");

        var files = state.GetProperty("files").EnumerateArray().ToList();
        await Assert.That(files.Select(f => f.GetProperty("path").GetString())).IsEquivalentTo(["docs/b.txt", "photos/a.jpg"]);
        await Assert.That(files.Single(f => f.GetProperty("path").GetString() == "photos/a.jpg").GetProperty("downloaded").GetBoolean()).IsTrue();
        await Assert.That(files.Single(f => f.GetProperty("path").GetString() == "docs/b.txt").GetProperty("downloaded").GetBoolean()).IsFalse();
        await Assert.That(files.All(f => f.GetProperty("ready").GetBoolean())).IsTrue();
        await Assert.That(files.Single(f => f.GetProperty("path").GetString() == "docs/b.txt").GetProperty("size").GetInt64()).IsEqualTo(500);
        await Assert.That(state.GetProperty("cleanedUp").GetBoolean()).IsFalse();

        var finishedAt = db.GetJob(jobId)!.FinishedAt!.Value;
        await Assert.That(state.GetProperty("cleanupAt").GetDateTimeOffset()).IsEqualTo(finishedAt.AddDays(7));

        db.SaveDownloadSettings(new DownloadSettings(false, 7));
        var disabled = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/download");
        await Assert.That(disabled.GetProperty("cleanupAt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Several_files_download_as_one_zip_with_repository_relative_entries()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, _) = await StartDownloadAsync(factory);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/jobs/{jobId}/download/content?set=all");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/zip");
        var started = db.GetJob(jobId)!.StartedAt!.Value.ToString("yyyy-MM-dd");
        await Assert.That(response.Content.Headers.ContentDisposition!.FileName).IsEqualTo($"itest-{started}.zip");

        await using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var contents = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            contents[entry.FullName] = await reader.ReadToEndAsync();
        }
        await Assert.That(contents.Keys.Order()).IsEquivalentTo(["docs/b.txt", "photos/a.jpg"]);
        await Assert.That(contents["photos/a.jpg"]).IsEqualTo("scripted:photos/a.jpg");
        await Assert.That(contents["docs/b.txt"]).IsEqualTo("scripted:docs/b.txt");
        await Assert.That(db.ListDownloadFiles(jobId).All(f => f.Downloaded)).IsTrue();
    }

    [Test]
    public async Task Remaining_with_one_file_left_serves_that_file_under_its_own_name()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, _) = await StartDownloadAsync(factory);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.MarkDownloadFilesDownloaded(jobId, ["photos/a.jpg"]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/jobs/{jobId}/download/content?set=remaining");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsNotEqualTo("application/zip");
        await Assert.That(response.Content.Headers.ContentDisposition!.FileName).IsEqualTo("b.txt");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("scripted:docs/b.txt");
        await Assert.That(db.ListDownloadFiles(jobId).All(f => f.Downloaded)).IsTrue();
    }

    [Test]
    public async Task Cleaning_up_an_in_progress_download_is_refused_and_keeps_its_files()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, run) = await StartDownloadAsync(factory, gated: true, awaitRun: false);
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        using var client = factory.CreateClient();

        try
        {
            await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "running" && staging.Exists(jobId), TimeSpan.FromSeconds(10));
            using var response = await client.PostAsync($"/api/jobs/{jobId}/download/cleanup", content: null);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(staging.Exists(jobId)).IsTrue();
        }
        finally
        {
            factory.Services.GetRequiredService<ScenarioGate>().Release(db.GetJob(jobId)!.RepositoryId);
            await run;
        }
    }

    [Test]
    public async Task A_cleaned_up_download_reports_cleaned_up_and_its_content_is_gone()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, _) = await StartDownloadAsync(factory);
        using var client = factory.CreateClient();

        using var cleanup = await client.PostAsync($"/api/jobs/{jobId}/download/cleanup", content: null);
        await Assert.That(cleanup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/download");
        await Assert.That(state.GetProperty("cleanedUp").GetBoolean()).IsTrue();
        await Assert.That(state.GetProperty("cleanupAt").ValueKind).IsEqualTo(JsonValueKind.Null);

        using var content = await client.GetAsync($"/api/jobs/{jobId}/download/content?set=all");
        await Assert.That(content.StatusCode).IsEqualTo(HttpStatusCode.Gone);
    }

    [Test]
    public async Task Pending_returns_the_latest_finished_download_with_files_left_until_all_are_downloaded()
    {
        await using var factory = new AriusApiFactory();
        var (repoId, jobId, _) = await StartDownloadAsync(factory);
        using var client = factory.CreateClient();

        var pending = await client.GetFromJsonAsync<JsonElement>($"/api/repos/{repoId}/downloads/pending");
        await Assert.That(pending.GetProperty("jobId").GetString()).IsEqualTo(jobId);
        await Assert.That(pending.GetProperty("readyFiles").GetInt32()).IsEqualTo(2);
        await Assert.That(pending.GetProperty("totalFiles").GetInt32()).IsEqualTo(2);
        await Assert.That(pending.GetProperty("remainingFiles").GetInt32()).IsEqualTo(2);
        await Assert.That(pending.GetProperty("remainingBytes").GetInt64()).IsEqualTo(1500);

        using var content = await client.GetAsync($"/api/jobs/{jobId}/download/content?set=all");
        await content.Content.ReadAsByteArrayAsync();

        using var after = await client.GetAsync($"/api/repos/{repoId}/downloads/pending");
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Content_of_a_job_that_is_not_a_download_or_a_set_that_does_not_exist_is_rejected()
    {
        await using var factory = new AriusApiFactory();
        var (_, jobId, _) = await StartDownloadAsync(factory);
        using var client = factory.CreateClient();

        using var missing = await client.GetAsync("/api/jobs/nope/download/content?set=all");
        using var badSet = await client.GetAsync($"/api/jobs/{jobId}/download/content?set=bogus");

        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(badSet.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
