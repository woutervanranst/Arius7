using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

internal class RepresentativeArchiveRestoreTests
{
    [Test]
    [CombinedDataSources]
    public async Task Canonical_Representative_Workflow_Runs_On_Supported_Backends(
        [ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)] [ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)] IE2EStorageBackend backend,
        CancellationToken cancellationToken)
    {
        if (backend is AzureE2EBackendFixture && !AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            RepresentativeWorkflowCatalog.Canonical,
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();

        if (backend.Capabilities.SupportsArchiveTier)
        {
            result.ArchiveTierOutcome.ShouldNotBeNull();
            result.ArchiveTierOutcome.PendingRehydratedBlobCount.ShouldBeGreaterThan(0);
            result.ArchiveTierOutcome.WasCostEstimateCaptured.ShouldBeTrue();
            result.ArchiveTierOutcome.RerunCopyCalls.ShouldBe(0);
        }
    }
}
