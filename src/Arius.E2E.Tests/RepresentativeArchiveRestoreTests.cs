using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

[ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)]
internal sealed class RepresentativeAzuriteArchiveRestoreTests(AzuriteE2EBackendFixture backend)
{
    [Test]
    public async Task Canonical_Representative_Workflow_Runs_On_Azurite_Backend(CancellationToken cancellationToken)
    {
        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            RepresentativeWorkflowCatalog.Canonical,
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();
    }
}

[ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)]
internal sealed class RepresentativeAzureArchiveRestoreTests(AzureE2EBackendFixture backend)
{
    [Test]
    public async Task Canonical_Representative_Workflow_Runs_On_Azure_Backend(CancellationToken cancellationToken)
    {
        if (!AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            RepresentativeWorkflowCatalog.Canonical,
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();
        result.ArchiveTierOutcome.ShouldNotBeNull();
        result.ArchiveTierOutcome.PendingRehydratedBlobCount.ShouldBeGreaterThan(0);
        result.ArchiveTierOutcome.WasCostEstimateCaptured.ShouldBeTrue();
        result.ArchiveTierOutcome.RerunCopyCalls.ShouldBe(0);
    }
}
