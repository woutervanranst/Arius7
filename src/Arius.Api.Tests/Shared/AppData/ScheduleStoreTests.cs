using Arius.Api.Shared.AppData;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class ScheduleStoreTests
{
    [Test]
    public async Task Inserted_schedule_is_listed_with_its_values_and_no_next_run()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        var id = temp.Database.InsertSchedule(repoId, "0 2 * * *", "archive", enabled: true);

        temp.Database.ListSchedules().ShouldBe([new ScheduleRecord(id, repoId, "0 2 * * *", "archive", true, null)]);
    }

    [Test]
    public async Task Disabled_schedule_round_trips_as_disabled()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        temp.Database.InsertSchedule(repoId, "* * * * *", "archive", enabled: false);

        temp.Database.ListSchedules().Single().Enabled.ShouldBeFalse();
    }

    [Test]
    public async Task Schedules_are_listed_in_insertion_order_with_distinct_ids()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        var first  = temp.Database.InsertSchedule(repoId, "0 1 * * *", "archive", true);
        var second = temp.Database.InsertSchedule(repoId, "0 2 * * *", "archive", true);

        second.ShouldNotBe(first);
        temp.Database.ListSchedules().Select(s => s.Id).ShouldBe([first, second]);
    }

    [Test]
    public async Task ListSchedules_filters_by_repository_when_one_is_given()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var repoA = temp.AddRepository(accountId, "a", "container-a");
        var repoB = temp.AddRepository(accountId, "b", "container-b");
        var forA = temp.Database.InsertSchedule(repoA, "0 1 * * *", "archive", true);
        var forB = temp.Database.InsertSchedule(repoB, "0 2 * * *", "archive", true);

        temp.Database.ListSchedules(repoA).Select(s => s.Id).ShouldBe([forA]);
        temp.Database.ListSchedules(repoB).Select(s => s.Id).ShouldBe([forB]);
        temp.Database.ListSchedules().Select(s => s.Id).ShouldBe([forA, forB]);
    }

    [Test]
    public async Task ListSchedules_for_a_repository_without_schedules_is_empty()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());

        temp.Database.ListSchedules(repoId).ShouldBeEmpty();
    }

    [Test]
    public async Task SetScheduleNextRun_stores_the_instant_and_null_clears_it()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        var id = temp.Database.InsertSchedule(repoId, "0 2 * * *", "archive", true);
        var nextRun = new DateTimeOffset(2030, 5, 6, 2, 0, 0, TimeSpan.Zero);

        temp.Database.SetScheduleNextRun(id, nextRun);
        temp.Database.ListSchedules().Single().NextRun.ShouldBe(nextRun);

        temp.Database.SetScheduleNextRun(id, null);
        temp.Database.ListSchedules().Single().NextRun.ShouldBeNull();
    }

    [Test]
    public async Task SetScheduleNextRun_changes_only_the_named_schedule()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        var target = temp.Database.InsertSchedule(repoId, "0 1 * * *", "archive", true);
        var other  = temp.Database.InsertSchedule(repoId, "0 2 * * *", "archive", true);

        temp.Database.SetScheduleNextRun(target, new DateTimeOffset(2030, 1, 1, 1, 0, 0, TimeSpan.Zero));

        temp.Database.ListSchedules().Single(s => s.Id == other).NextRun.ShouldBeNull();
    }

    [Test]
    public async Task DeleteSchedule_removes_only_that_schedule()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        var doomed = temp.Database.InsertSchedule(repoId, "0 1 * * *", "archive", true);
        var kept   = temp.Database.InsertSchedule(repoId, "0 2 * * *", "archive", true);

        temp.Database.DeleteSchedule(doomed);

        temp.Database.ListSchedules().Select(s => s.Id).ShouldBe([kept]);
    }

    [Test]
    public async Task DeleteSchedule_of_an_unknown_id_is_a_no_op()
    {
        using var temp = new TempAppDatabase();
        var repoId = temp.AddRepository(temp.AddAccount());
        var id = temp.Database.InsertSchedule(repoId, "0 1 * * *", "archive", true);

        Should.NotThrow(() => temp.Database.DeleteSchedule(id + 100));

        temp.Database.ListSchedules().Count.ShouldBe(1);
    }

    [Test]
    public async Task Deleting_a_repository_removes_its_schedules_but_not_other_repositories_schedules()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var doomedRepo = temp.AddRepository(accountId, "doomed", "container-doomed");
        var keptRepo   = temp.AddRepository(accountId, "kept", "container-kept");
        temp.Database.InsertSchedule(doomedRepo, "0 1 * * *", "archive", true);
        var kept = temp.Database.InsertSchedule(keptRepo, "0 2 * * *", "archive", true);

        temp.Database.DeleteRepository(doomedRepo);

        temp.Database.ListSchedules().Select(s => s.Id).ShouldBe([kept]);
    }
}
