using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

[ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
internal class ArchiveTierRepresentativeTests(AzureE2EBackendFixture backend)
{
    [Test]
    public async Task ArchiveTier_Planning_And_PendingVsReady_Are_Reported(CancellationToken cancellationToken)
    {
        if (!AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live archive-tier representative coverage");
            return;
        }

        var workflow = new RepresentativeWorkflowDefinition(
            "archive-tier-representative-workflow",
            SyntheticRepositoryProfile.Small,
            20260419,
            []);

        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            workflow,
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();
    }
}
