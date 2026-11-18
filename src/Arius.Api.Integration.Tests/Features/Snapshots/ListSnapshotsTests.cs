using System.Net;
using System.Net.Http.Json;
using Arius.Api.FakeTestHost;
using Arius.Api.Features.Snapshots;
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.SnapshotsListQuery;

namespace Arius.Api.Integration.Tests.Features.Snapshots;

/// <summary><c>GET /repos/{id}/snapshots</c> — the repository's snapshots for time travel.</summary>
public class ListSnapshotsTests
{
    [Test]
    public async Task Lists_the_repository_snapshots_with_their_time_and_file_count()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var first  = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2024, 2, 1, 10, 0, 0, TimeSpan.Zero);
        factory.Scenarios.SetRead(repoId, new ReadScenario(Snapshots:
        [
            new SnapshotInfo("2024-01-01T100000.000Z", first, 10),
            new SnapshotInfo("2024-02-01T100000.000Z", second, 12),
        ]));
        using var client = factory.CreateClient();

        var snapshots = await client.GetFromJsonAsync<List<SnapshotDto>>($"/api/repos/{repoId}/snapshots");

        await Assert.That(snapshots).IsEquivalentTo([
            new SnapshotDto("2024-01-01T100000.000Z", first, 10),
            new SnapshotDto("2024-02-01T100000.000Z", second, 12),
        ]);
    }

    [Test]
    public async Task A_repository_whose_container_does_not_exist_yet_has_no_snapshots()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRead(repoId, new ReadScenario(ContainerMissing: true));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/repos/{repoId}/snapshots");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadFromJsonAsync<List<SnapshotDto>>()).IsEmpty();
    }
}
