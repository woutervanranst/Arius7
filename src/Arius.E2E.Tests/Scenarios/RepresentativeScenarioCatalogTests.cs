using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioCatalogTests
{
    [Test]
    public async Task Catalog_MatchesApprovedScenarioDefinitions()
    {
        await Task.CompletedTask;

        var scenarios = RepresentativeScenarioCatalog.All;

        scenarios.ShouldBe([
            new RepresentativeScenarioDefinition(
                "initial-archive-v1",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                ArchiveMode: ScenarioArchiveMode.Initial),
            new RepresentativeScenarioDefinition(
                "incremental-archive-v2",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm,
                ArchiveMode: ScenarioArchiveMode.Incremental),
            new RepresentativeScenarioDefinition(
                "second-archive-no-changes",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm,
                ArchiveMode: ScenarioArchiveMode.NoChanges),
            new RepresentativeScenarioDefinition(
                "restore-latest-cold-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold,
                RestoreTarget: ScenarioRestoreTarget.Latest),
            new RepresentativeScenarioDefinition(
                "restore-latest-warm-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm,
                RestoreTarget: ScenarioRestoreTarget.Latest),
            new RepresentativeScenarioDefinition(
                "restore-previous-cold-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                RestoreTarget: ScenarioRestoreTarget.Previous),
            new RepresentativeScenarioDefinition(
                "restore-previous-warm-cache",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Warm,
                RestoreTarget: ScenarioRestoreTarget.Previous),
            new RepresentativeScenarioDefinition(
                "restore-multiple-versions",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Warm,
                RestoreTarget: ScenarioRestoreTarget.MultipleVersions),
            new RepresentativeScenarioDefinition(
                "restore-local-conflict-no-overwrite",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold,
                UseOverwrite: false,
                RestoreTarget: ScenarioRestoreTarget.Latest),
            new RepresentativeScenarioDefinition(
                "restore-local-conflict-overwrite",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V2,
                ScenarioCacheState.Cold,
                UseOverwrite: true,
                RestoreTarget: ScenarioRestoreTarget.Latest),
            new RepresentativeScenarioDefinition(
                "archive-no-pointers",
                ScenarioOperation.Archive,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                UseNoPointers: true,
                ArchiveMode: ScenarioArchiveMode.Initial),
            new RepresentativeScenarioDefinition(
                "archive-remove-local-then-thin-followup",
                ScenarioOperation.ArchiveThenRestore,
                ScenarioBackendRequirement.Any,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                UseRemoveLocal: true,
                ArchiveMode: ScenarioArchiveMode.Initial,
                RestoreTarget: ScenarioRestoreTarget.Latest),
            new RepresentativeScenarioDefinition(
                "archive-tier-planning",
                ScenarioOperation.Restore,
                ScenarioBackendRequirement.AzureArchiveCapable,
                SyntheticRepositoryVersion.V1,
                ScenarioCacheState.Cold,
                RestoreTarget: ScenarioRestoreTarget.Latest),
        ]);
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
