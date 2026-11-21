using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Schedules;
using Arius.Api.Integration.Tests.Harness;

namespace Arius.Api.Integration.Tests.Features.Schedules;

/// <summary>The <c>/repos/{id}/schedules</c> endpoints: create, list and delete a repository's archive schedules.</summary>
public class ScheduleEndpointsTests
{
    [Test]
    public async Task A_created_schedule_defaults_to_an_enabled_archive_and_is_listed_until_deleted()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        using var created = await client.PostAsJsonAsync($"/api/repos/{repoId}/schedules", new CreateScheduleRequest("0 3 * * *", Kind: null));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var schedule = await created.Content.ReadFromJsonAsync<ScheduleDto>();
        await Assert.That(schedule).IsEqualTo(new ScheduleDto(schedule!.Id, repoId, "0 3 * * *", "archive", Enabled: true, NextRun: null));
        await Assert.That(created.Headers.Location!.OriginalString).IsEqualTo($"/api/repos/{repoId}/schedules/{schedule.Id}");
        await Assert.That(await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/repos/{repoId}/schedules")).IsEquivalentTo([schedule]);

        using var deleted = await client.DeleteAsync($"/api/repos/{repoId}/schedules/{schedule.Id}");

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/repos/{repoId}/schedules")).IsEmpty();
    }

    [Test]
    public async Task A_schedule_for_an_unknown_repository_is_404()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/repos/999/schedules", new CreateScheduleRequest("0 3 * * *", "archive"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
