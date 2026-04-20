using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Scenarios;

namespace Arius.E2E.Tests;

[ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
internal class ArchiveTierRepresentativeTests(AzureE2EBackendFixture backend)
{
    [Test]
    public async Task ArchiveTier_Planning_And_PendingVsReady_Are_Reported(CancellationToken cancellationToken)
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-tier-planning");

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 20260419,
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();
        result.ArchiveTierOutcome.ShouldNotBeNull();
        result.ArchiveTierOutcome.WasCostEstimateCaptured.ShouldBeTrue();
        result.ArchiveTierOutcome.InitialPendingChunks.ShouldBeGreaterThan(0);
        result.ArchiveTierOutcome.InitialFilesRestored.ShouldBe(0);
        result.ArchiveTierOutcome.PendingChunksOnRerun.ShouldBeGreaterThan(0);
        result.ArchiveTierOutcome.RerunCopyCalls.ShouldBe(0);
        result.ArchiveTierOutcome.ReadyFilesRestored.ShouldBeGreaterThan(0);
        result.ArchiveTierOutcome.ReadyPendingChunks.ShouldBe(0);
        result.ArchiveTierOutcome.CleanupDeletedChunks.ShouldBeGreaterThan(0);
    }
}
