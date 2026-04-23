using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Scenarios;
using Arius.E2E.Tests.Workflows;

namespace Arius.E2E.Tests;

internal class RepresentativeArchiveRestoreTests
{
    [Test]
    [CombinedDataSources]
    public async Task Representative_Scenario_Runs_OnSupportedBackends(
        [ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)] [ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)] IE2EStorageBackend backend,
        [MethodDataSource(typeof(RepresentativeWorkflowCatalog), nameof(RepresentativeWorkflowCatalog.All))] RepresentativeWorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        if (backend is AzureE2EBackendFixture && !AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        if (ShouldSkipForAzureColdRestoreTimeout(backend, workflow))
        {
            Skip.Unless(false, $"Azure cold restore representative scenario is tracked by issue #65: {workflow.Name}");
            return;
        }

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            workflow,
            SyntheticRepositoryProfile.Representative,
            seed: 20260419,
            dependencies: new RepresentativeScenarioRunnerDependencies
            {
                AssertRestoreTrees = true,
            },
            cancellationToken: cancellationToken);

        if (workflow.BackendRequirement == RepresentativeWorkflowBackendRequirement.Any)
            result.WasSkipped.ShouldBeFalse();
    }

    static bool ShouldSkipForAzureColdRestoreTimeout(IE2EStorageBackend backend, RepresentativeWorkflowDefinition workflow)
    {
        if (backend is not AzureE2EBackendFixture)
            return false;

        return workflow == RepresentativeWorkflowCatalog.RestoreLatestColdCache ||
               workflow == RepresentativeWorkflowCatalog.RestorePreviousColdCache ||
               workflow == RepresentativeWorkflowCatalog.RestoreLocalConflictNoOverwrite ||
               workflow == RepresentativeWorkflowCatalog.RestoreLocalConflictOverwrite ||
               workflow == RepresentativeWorkflowCatalog.ArchiveTierPlanning;
    }
}
