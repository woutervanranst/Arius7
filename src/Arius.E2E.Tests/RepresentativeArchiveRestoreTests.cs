using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

internal class RepresentativeArchiveRestoreTests
{
    [Test]
    [CombinedDataSources]
    public async Task Representative_Workflow_Runs_OnSupportedBackends(
        [ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)] [ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)] IE2EStorageBackend backend,
        [MethodDataSource(typeof(RepresentativeWorkflowCatalog), nameof(RepresentativeWorkflowCatalog.All))] RepresentativeWorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        if (backend is AzureE2EBackendFixture && !AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        var result = await RepresentativeWorkflowRunner.RunAsync(
            backend,
            workflow,
            dependencies: new RepresentativeWorkflowRunnerDependencies
            {
                AssertRestoreTrees = true,
            },
            cancellationToken: cancellationToken);

        result.WasSkipped.ShouldBeFalse();
    }
}
