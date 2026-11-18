using Arius.Api.FakeTestHost;
using Arius.Api.Features.Downloads;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class DownloadJobTests
{
    [Test]
    public async Task A_download_restores_only_into_its_staging_folder_with_fixed_options()
    {
        await using var factory = new AriusApiFactory();
        var localFolder = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: localFolder);

        var scenario = CanonicalScenarios.OnlineDownload();
        factory.Scenarios.SetRestore(repoId, scenario);

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("photos/a.jpg", 1000), new DownloadFile("docs/b.txt", 500)]);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.Standard);
        await run;

        var options = scenario.Received.Single().Options;
        await Assert.That(options.RootDirectory).IsEqualTo(staging.PathFor(jobId));
        await Assert.That(options.Overwrite).IsFalse();
        await Assert.That(options.NoPointers).IsTrue();
        await Assert.That(options.Version).IsEqualTo("v1");
        await Assert.That(options.TargetPaths.SequenceEqual([RelativePath.Parse("photos/a.jpg"), RelativePath.Parse("docs/b.txt")])).IsTrue();

        var job = db.GetJob(jobId)!;
        await Assert.That(job.Kind).IsEqualTo("download");
        await Assert.That(job.Status).IsEqualTo("completed");
        await Assert.That(db.ListDownloadFiles(jobId).All(f => f.Ready)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(staging.PathFor(jobId), "photos", "a.jpg"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(staging.PathFor(jobId), "docs", "b.txt"))).IsTrue();
        await Assert.That(!Directory.Exists(localFolder) || !Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories).Any()).IsTrue();
    }

    [Test]
    public async Task An_online_download_below_the_auto_accept_limit_stages_its_files_without_asking()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var online = CanonicalScenarios.OnlineDownload();
        factory.Scenarios.SetRestore(repoId, online with { CostPrompt = online.CostPrompt! with { TotalStandard = 0.005, TotalHigh = 0.005 } });

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        await factory.Services.GetRequiredService<JobRunner>()
            .RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("photos/a.jpg", 1000), new DownloadFile("docs/b.txt", 500)])
            .WaitAsync(TimeSpan.FromSeconds(10));   // asking would park the run at awaiting-cost

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
        await Assert.That(db.ListDownloadFiles(jobId).All(f => f.Ready)).IsTrue();
    }

    [Test]
    public async Task A_download_declined_at_the_cost_prompt_leaves_no_staging_folder()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.OnlineDownload());

        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var jobId = Guid.NewGuid().ToString();

        var run = factory.Services.GetRequiredService<JobRunner>()
            .RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("photos/a.jpg", 1000)]);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, null);
        await run;

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
        await Assert.That(staging.Exists(jobId)).IsFalse();
    }

    [Test]
    public async Task Ready_files_survive_a_resume_and_unready_staged_files_are_pruned_first()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();

        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingDownloadStaysPending());

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("photos/a.jpg", 1000), new DownloadFile("docs/b.txt", 500)]);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.Standard);
        await run;

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("rehydrating");
        await Assert.That(db.ListDownloadFiles(jobId).Single(f => f.Path == "photos/a.jpg").Ready).IsTrue();
        await Assert.That(db.ListDownloadFiles(jobId).Single(f => f.Path == "docs/b.txt").Ready).IsFalse();

        var strayFile = Path.Combine(staging.PathFor(jobId), "docs", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(strayFile)!);
        await File.WriteAllTextAsync(strayFile, "half");

        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingDownloadStaysPending() with
        {
            PostApproveEvents = [],
            Result = new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 1 },
        });
        await runner.ResumeRestoreAsync(jobId);

        await Assert.That(File.Exists(strayFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(staging.PathFor(jobId), "photos", "a.jpg"))).IsTrue();
        await Assert.That(db.ListDownloadFiles(jobId).Single(f => f.Path == "photos/a.jpg").Ready).IsTrue();
    }

    [Test]
    public async Task A_resume_pass_marks_files_restored_after_rehydration_ready_and_completes_the_job()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();

        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingDownloadStaysPending());

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var staging = factory.Services.GetRequiredService<DownloadStaging>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("photos/a.jpg", 1000), new DownloadFile("docs/b.txt", 500)]);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.Standard);
        await run;
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("rehydrating");

        // The archive-tier chunk is now rehydrated: the resume pass restores the remaining file.
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingDownloadStaysPending() with
        {
            PostApproveEvents = [ new FileRestoredEvent(RelativePath.Parse("docs/b.txt"), 500) ],
            Result = new RestoreResult { Success = true, FilesRestored = 1, FilesSkipped = 0, ChunksPendingRehydration = 0 },
        });
        await runner.ResumeRestoreAsync(jobId);

        await Assert.That(db.ListDownloadFiles(jobId).All(f => f.Ready)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(staging.PathFor(jobId), "docs", "b.txt"))).IsTrue();
        var job = db.GetJob(jobId)!;
        await Assert.That(job.Status).IsEqualTo("completed");
        await Assert.That(job.Detail).IsEqualTo("Download complete.");
    }

    [Test]
    public async Task A_download_whose_files_cannot_be_recorded_fails_instead_of_leaving_the_job_running()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        // The same path twice violates the (job_id, path) key, so recording the collected files throws.
        await runner.RunDownloadAsync(repoId, jobId, "v1", [new DownloadFile("a.txt", 1), new DownloadFile("a.txt", 1)]);

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("failed");
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }

    [Test]
    [Arguments(null, "a", "Choose a snapshot to download from.")]
    [Arguments("v1", null, "Collect at least one file to download.")]
    [Arguments("v1", "../x", "'../x' is not a valid repository path.")]
    [Arguments("v1", "", "'' is not a valid repository path.")]
    public async Task StartDownload_rejects_a_missing_version_no_files_and_a_path_outside_the_repository(string? version, string? path, string expectedMessage)
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();

        var handler = factory.Server.CreateHandler();
        await using var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/arius", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        await conn.StartAsync();

        DownloadFileRequest[] files = path is null ? [] : [new DownloadFileRequest(path, 1)];
        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync<string>("StartDownload", repoId, version, files));

        await Assert.That(ex!.Message).Contains(expectedMessage);
        await Assert.That(db.ListJobs().Count).IsEqualTo(0);
    }
}
