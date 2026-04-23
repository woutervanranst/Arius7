using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Scenarios;

namespace Arius.E2E.Tests;

internal class RepresentativeArchiveRestoreTests
{
    [Test]
    [CombinedDataSources]
    public async Task Representative_Scenario_Runs_OnSupportedBackends(
        [ClassDataSource<AzuriteE2EBackendFixture>(Shared = SharedType.PerTestSession)] [ClassDataSource<AzureE2EBackendFixture>(Shared = SharedType.PerTestSession)] IE2EStorageBackend backend,
        [MethodDataSource(typeof(RepresentativeScenarioCatalog), nameof(RepresentativeScenarioCatalog.All))] RepresentativeScenarioDefinition scenario,
        CancellationToken cancellationToken)
    {
        if (backend is AzureE2EBackendFixture && !AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live representative backend coverage");
            return;
        }

        if (ShouldSkipForAzureColdRestoreTimeout(backend, scenario))
        {
            Skip.Unless(false, $"Azure cold restore representative scenario is tracked by issue #65: {scenario.Name}");
            return;
        }

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Representative,
            seed: 20260419,
            dependencies: new RepresentativeScenarioRunnerDependencies
            {
                AssertRestoreTrees = true,
            },
            cancellationToken: cancellationToken);

        if (scenario.BackendRequirement == ScenarioBackendRequirement.Any)
            result.WasSkipped.ShouldBeFalse();
    }

    static bool ShouldSkipForAzureColdRestoreTimeout(IE2EStorageBackend backend, RepresentativeScenarioDefinition scenario)
    {
        if (backend is not AzureE2EBackendFixture)
            return false;

        return scenario.Name is
            "restore-latest-cold-cache" or
            "restore-previous-cold-cache" or
            "restore-local-conflict-no-overwrite" or
            "restore-local-conflict-overwrite" or
            "archive-tier-planning";
    }
}
