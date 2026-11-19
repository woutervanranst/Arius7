using System.Net;
using System.Net.Http.Json;
using Arius.Api.FakeTestHost;
using Arius.Api.Features.Statistics;
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Integration.Tests.Features.Statistics;

/// <summary><c>GET /repos/{id}/stats</c>: statistics are computed by Core on a cache miss and cached per version and
/// coverage, so a repeat request does not touch storage.</summary>
public class GetStatisticsTests
{
    private static readonly RepositoryStatistics Computed = new(
        Files: 12, OriginalSize: 5000, DeduplicatedSize: 4000, StoredSize: 3000, UniqueChunks: 7, TotalStorageCostPerMonth: 0.25,
        StoredByTier:
        [
            new TierStorageCost(BlobTier.Hot, UniqueChunks: 2, StoredSize: 1000, CostPerMonth: 0.05),
            new TierStorageCost(BlobTier.Archive, UniqueChunks: 5, StoredSize: 2000, CostPerMonth: 0.20),
        ]);

    [Test]
    public async Task A_miss_is_computed_by_Core_and_a_repeat_is_served_from_the_cache_per_version_and_coverage()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var scenario = new ReadScenario(Statistics: Computed, LatestVersion: "v2");
        factory.Scenarios.SetRead(repoId, scenario);
        using var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<StatisticsDto>($"/api/repos/{repoId}/stats?version=v1");

        await AssertComputed(first!);
        await Assert.That(scenario.Received.OfType<StatisticsQuery>()).IsEquivalentTo([new StatisticsQuery("v1", EnsureFullCoverage: false)]);
        await Assert.That(scenario.Received.OfType<LatestSnapshotVersionQuery>().Count()).IsEqualTo(1);

        var repeat = await client.GetFromJsonAsync<StatisticsDto>($"/api/repos/{repoId}/stats?version=v1");

        await AssertComputed(repeat!);
        await Assert.That(scenario.Received.OfType<StatisticsQuery>().Count()).IsEqualTo(1);   // served from the cache

        await client.GetFromJsonAsync<StatisticsDto>($"/api/repos/{repoId}/stats?version=v1&full=true");

        await Assert.That(scenario.Received.OfType<StatisticsQuery>())
            .IsEquivalentTo([new StatisticsQuery("v1", EnsureFullCoverage: false), new StatisticsQuery("v1", EnsureFullCoverage: true)]);
    }

    [Test]
    public async Task A_repository_whose_container_does_not_exist_yet_reports_zeroed_statistics()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetRead(repoId, new ReadScenario(ContainerMissing: true));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/repos/{repoId}/stats");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var stats = await response.Content.ReadFromJsonAsync<StatisticsDto>();
        await Assert.That(Totals(stats!)).IsEqualTo((0L, 0L, 0L, 0L, 0L, 0d));
        await Assert.That(stats!.StoredByTier).IsEmpty();
    }

    private static (long, long, long, long, long, double) Totals(StatisticsDto dto) =>
        (dto.Files, dto.OriginalSize, dto.DeduplicatedSize, dto.StoredSize, dto.UniqueChunks, dto.TotalStorageCostPerMonth);

    private static async Task AssertComputed(StatisticsDto dto)
    {
        await Assert.That(Totals(dto)).IsEqualTo((12L, 5000L, 4000L, 3000L, 7L, 0.25));
        await Assert.That(dto.StoredByTier).IsEquivalentTo([
            new TierStatisticsDto("Hot", UniqueChunks: 2, StoredSize: 1000, CostPerMonth: 0.05),
            new TierStatisticsDto("Archive", UniqueChunks: 5, StoredSize: 2000, CostPerMonth: 0.20),
        ]);
    }
}
