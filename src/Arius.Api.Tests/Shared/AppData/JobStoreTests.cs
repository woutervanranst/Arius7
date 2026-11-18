using Arius.Api.Shared.AppData;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class JobStoreTests
{
    [Test]
    public async Task Inserted_job_starts_at_zero_percent_without_detail_state_or_outcome()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        temp.Database.InsertJob("j", repoId, "archive", "schedule", "running");

        var job = temp.Database.GetJob("j")!;
        job.RepositoryId.ShouldBe(repoId);
        job.Kind.ShouldBe("archive");
        job.Trigger.ShouldBe("schedule");
        job.Status.ShouldBe("running");
        job.Pct.ShouldBe(0);
        job.Detail.ShouldBeNull();
        job.StartedAt.ShouldNotBeNull();
        job.FinishedAt.ShouldBeNull();
        job.StateJson.ShouldBeNull();
        job.Outcome.ShouldBeNull();
    }

    [Test]
    public async Task GetJob_of_an_unknown_id_is_null()
    {
        using var temp = new TempAppDatabase();

        temp.Database.GetJob("nope").ShouldBeNull();
    }

    [Test]
    public async Task CompleteJob_reports_whether_it_applied_the_transition()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "archive", "one-off", "running");

        temp.Database.CompleteJob("j", "completed", 100, "done").ShouldBeTrue();
        temp.Database.CompleteJob("j", "cancelled", 0, "late cancel").ShouldBeFalse();
        temp.Database.CompleteJob("missing", "completed", 100, null).ShouldBeFalse();

        var job = temp.Database.GetJob("j")!;
        job.Status.ShouldBe("completed");
        job.Detail.ShouldBe("done");
        job.FinishedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task SetJobStatus_without_detail_keeps_the_existing_detail()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "restore", "one-off", "running");
        temp.Database.SetJobStatus("j", "awaiting-cost", "Approve the cost");

        temp.Database.SetJobStatus("j", "rehydrating");

        var job = temp.Database.GetJob("j")!;
        job.Status.ShouldBe("rehydrating");
        job.Detail.ShouldBe("Approve the cost");
    }

    [Test]
    public async Task TryResumeToRunning_flips_a_parked_restore_and_sets_the_detail()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "restore", "one-off", "running");
        temp.Database.SetJobStatus("j", "rehydrating");

        temp.Database.TryResumeToRunning("j", "Resuming").ShouldBeTrue();

        var job = temp.Database.GetJob("j")!;
        job.Status.ShouldBe("running");
        job.Detail.ShouldBe("Resuming");
    }

    [Test]
    public async Task TryResumeToRunning_resumes_an_awaiting_cost_job_and_clears_the_detail_when_none_is_given()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "restore", "one-off", "running");
        temp.Database.SetJobStatus("j", "awaiting-cost", "Approve the cost");

        temp.Database.TryResumeToRunning("j").ShouldBeTrue();

        var job = temp.Database.GetJob("j")!;
        job.Status.ShouldBe("running");
        job.Detail.ShouldBeNull();
    }

    [Test]
    public async Task TryResumeToRunning_does_not_resurrect_a_terminal_job()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "restore", "one-off", "running");
        temp.Database.SetJobStatus("j", "rehydrating");
        temp.Database.CompleteJob("j", "cancelled", 0, "Cancelled.");

        temp.Database.TryResumeToRunning("j", "Resuming").ShouldBeFalse();

        temp.Database.GetJob("j")!.Status.ShouldBe("cancelled");
        temp.Database.HasActiveJob(repoId).ShouldBeFalse();
    }

    [Test]
    public async Task TryResumeToRunning_leaves_an_already_running_job_alone()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "restore", "one-off", "running");

        temp.Database.TryResumeToRunning("j", "Resuming").ShouldBeFalse();

        temp.Database.GetJob("j")!.Detail.ShouldBeNull();
    }

    [Test]
    public async Task ListStaleAwaitingCost_returns_only_awaiting_cost_jobs_started_before_the_cutoff()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var awaitingRepo   = temp.AddRepository(accountId, "a", "c1");
        var rehydratingRepo = temp.AddRepository(accountId, "b", "c2");
        var runningRepo    = temp.AddRepository(accountId, "c", "c3");
        temp.Database.InsertJob("awaiting", awaitingRepo, "restore", "one-off", "running");
        temp.Database.SetJobStatus("awaiting", "awaiting-cost");
        temp.Database.InsertJob("rehydrating", rehydratingRepo, "restore", "one-off", "running");
        temp.Database.SetJobStatus("rehydrating", "rehydrating");
        temp.Database.InsertJob("running", runningRepo, "restore", "one-off", "running");

        temp.Database.ListStaleAwaitingCost(DateTimeOffset.UtcNow.AddHours(1)).Select(j => j.Id).ShouldBe(["awaiting"]);
        temp.Database.ListStaleAwaitingCost(DateTimeOffset.UtcNow.AddHours(-1)).ShouldBeEmpty();
    }

    [Test]
    public async Task ListActiveJobs_returns_non_terminal_jobs_and_can_be_scoped_to_a_repository()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var repoA = temp.AddRepository(accountId, "a", "c1");
        var repoB = temp.AddRepository(accountId, "b", "c2");
        var repoC = temp.AddRepository(accountId, "c", "c3");
        temp.Database.InsertJob("finished", repoA, "archive", "one-off", "running");
        temp.Database.CompleteJob("finished", "completed", 100, null);
        temp.Database.InsertJob("active-a", repoA, "archive", "one-off", "running");
        temp.Database.InsertJob("active-b", repoB, "restore", "one-off", "running");
        temp.Database.SetJobStatus("active-b", "rehydrating");
        temp.Database.InsertJob("done-c", repoC, "archive", "one-off", "running");
        temp.Database.CompleteJob("done-c", "failed", 10, "boom");

        temp.Database.ListActiveJobs().Select(j => j.Id).ShouldBe(["active-a", "active-b"], ignoreOrder: true);
        temp.Database.ListActiveJobs(repoA).Select(j => j.Id).ShouldBe(["active-a"]);
        temp.Database.ListActiveJobs(repoC).ShouldBeEmpty();
    }

    [Test]
    public async Task ListActiveJobs_is_not_capped_by_the_job_history_window()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var repoId = temp.AddRepository(accountId, "a", "c1");
        var activeRepo = temp.AddRepository(accountId, "b", "c2");
        temp.Database.InsertJob("old-active", activeRepo, "restore", "one-off", "running");
        await Task.Delay(5);
        for (var i = 0; i < 3; i++)
        {
            temp.Database.InsertJob($"newer-{i}", repoId, "archive", "one-off", "running");
            temp.Database.CompleteJob($"newer-{i}", "completed", 100, null);
        }

        temp.Database.ListJobs(limit: 3).Select(j => j.Id).ShouldNotContain("old-active");
        temp.Database.ListActiveJobs().Select(j => j.Id).ShouldBe(["old-active"]);
    }

    [Test]
    public async Task ListJobs_returns_the_newest_jobs_first_up_to_the_limit()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        foreach (var id in new[] { "first", "second", "third" })
        {
            temp.Database.InsertJob(id, repoId, "archive", "one-off", "running");
            temp.Database.CompleteJob(id, "completed", 100, null);
            await Task.Delay(5);
        }

        temp.Database.ListJobs(limit: 2).Select(j => j.Id).ShouldBe(["third", "second"]);
        temp.Database.ListJobs().Select(j => j.Id).ShouldBe(["third", "second", "first"]);
    }

    [Test]
    public async Task SaveJobState_stores_the_latest_snapshot_json_and_overwrites_the_previous_one()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        temp.Database.InsertJob("j", repoId, "archive", "one-off", "running");

        temp.Database.SaveJobState("j", """{"pct":10}""");
        temp.Database.SaveJobState("j", """{"pct":20}""");

        temp.Database.GetJob("j")!.StateJson.ShouldBe("""{"pct":20}""");
    }

    [Test]
    public async Task SetJobOutcome_stores_the_terminal_outcome_json_for_that_job_only()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var repoA = temp.AddRepository(accountId, "a", "c1");
        var repoB = temp.AddRepository(accountId, "b", "c2");
        temp.Database.InsertJob("a", repoA, "archive", "one-off", "running");
        temp.Database.InsertJob("b", repoB, "archive", "one-off", "running");

        temp.Database.SetJobOutcome("a", """{"result":"ok"}""");

        temp.Database.GetJob("a")!.Outcome.ShouldBe("""{"result":"ok"}""");
        temp.Database.GetJob("b")!.Outcome.ShouldBeNull();
    }

    [Test]
    public async Task HasActiveJob_is_false_for_a_repository_without_jobs()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        temp.Database.HasActiveJob(repoId).ShouldBeFalse();
    }

    [Test]
    public async Task ResetAll_removes_every_app_row()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var repoId = temp.AddRepository(accountId);
        temp.Database.InsertJob("j", repoId, "download", "one-off", "running");
        temp.Database.InsertDownloadFiles("j", [new("a.txt", 1)]);
        temp.Database.InsertSchedule(repoId, "0 1 * * *", "archive", true);
        temp.Database.ReplaceGlobalDefaults(DefaultValues.None with { Tier = "cool" });
        temp.Database.ReplaceRepositoryDefaults(repoId, DefaultValues.None with { FastHash = true });

        temp.Database.ResetAll();

        temp.Database.ListAccounts().ShouldBeEmpty();
        temp.Database.ListRepositories().ShouldBeEmpty();
        temp.Database.ListJobs().ShouldBeEmpty();
        temp.Database.ListSchedules().ShouldBeEmpty();
        temp.Database.GetGlobalDefaults().ShouldBe(DefaultValues.None);
    }
}
