using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioCatalogTests
{
    [Test]
    public async Task Catalog_MatchesApprovedScenarioDefinitions()
    {
        await Task.CompletedTask;

        RepresentativeScenarioCatalog.All.Select(ToContract).ShouldBe([
            ToContract(new RepresentativeScenarioDefinition(
                "initial-archive-v1",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold)
            {
                ArchiveMode = ScenarioArchiveMode.Initial,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "incremental-archive-v2",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm)
            {
                ArchiveMode = ScenarioArchiveMode.Incremental,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "second-archive-no-changes",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm)
            {
                ArchiveMode = ScenarioArchiveMode.NoChanges,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-latest-cold-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold)
            {
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-latest-warm-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm)
            {
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-previous-cold-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                RestoreVersion: "previous")
            {
                RestoreTarget = ScenarioRestoreTarget.Previous,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-previous-warm-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Warm,
                RestoreVersion: "previous")
            {
                RestoreTarget = ScenarioRestoreTarget.Previous,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-multiple-versions",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm)
            {
                RestoreTarget = ScenarioRestoreTarget.MultipleVersions,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-local-conflict-no-overwrite",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold,
                UseOverwrite: false)
            {
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "restore-local-conflict-overwrite",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold,
                UseOverwrite: true)
            {
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "archive-no-pointers",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                UseNoPointers: true)
            {
                ArchiveMode = ScenarioArchiveMode.Initial,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "archive-remove-local-then-thin-followup",
                ScenarioOperation.ArchiveThenRestore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                UseRemoveLocal: true)
            {
                ArchiveMode = ScenarioArchiveMode.Initial,
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
            ToContract(new RepresentativeScenarioDefinition(
                "archive-tier-planning",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.AzureArchiveCapable,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold)
            {
                RestoreTarget = ScenarioRestoreTarget.Latest,
            }),
        ]);

        static object ToContract(RepresentativeScenarioDefinition scenario) => new
        {
            scenario.Name,
            scenario.Operation,
            scenario.BackendRequirement,
            scenario.SourceVersion,
            scenario.CacheState,
            scenario.UseNoPointers,
            scenario.UseRemoveLocal,
            scenario.UseOverwrite,
            scenario.RestoreVersion,
            scenario.ArchiveMode,
            scenario.RestoreTarget,
        };
    }

    [Test]
    public async Task ScenarioDefinition_PreservesPlannedPositionalRestoreVersion_AndAllowsTypedMetadata()
    {
        await Task.CompletedTask;

        var scenario = new RepresentativeScenarioDefinition(
            "restore-previous-warm-cache",
            ScenarioOperation.Restore,
            ScenarioBackendRequirement.Any,
            SyntheticRepositoryVersion.V1,
            ScenarioCacheState.Warm,
            RestoreVersion: "previous")
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        };

        scenario.RestoreVersion.ShouldBe("previous");
        scenario.RestoreTarget.ShouldBe(ScenarioRestoreTarget.Previous);
    }

    [Test]
    public async Task Catalog_UsesUniqueScenarioNames_AndDistinctStructuredMetadata()
    {
        await Task.CompletedTask;

        var scenarios = RepresentativeScenarioCatalog.All;

        scenarios.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count().ShouldBe(scenarios.Count);

        var incrementalArchive = scenarios.Single(x => x.Name == "incremental-archive-v2");
        var secondArchive = scenarios.Single(x => x.Name == "second-archive-no-changes");
        var latestRestore = scenarios.Single(x => x.Name == "restore-latest-warm-cache");
        var multipleVersionsRestore = scenarios.Single(x => x.Name == "restore-multiple-versions");

        incrementalArchive.ArchiveMode.ShouldNotBe(secondArchive.ArchiveMode);
        latestRestore.RestoreTarget.ShouldNotBe(multipleVersionsRestore.RestoreTarget);
    }
}
