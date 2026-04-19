using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

internal static class RepresentativeScenarioCatalog
{
    public static IReadOnlyList<RepresentativeScenarioDefinition> All { get; } =
    [
        new("initial-archive-v1", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold),
        new("incremental-archive-v2", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.Incremental,
        },
        new("second-archive-no-changes", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.NoChanges,
        },
        new("restore-latest-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
        new("restore-latest-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
        new("restore-previous-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, RestoreVersion: "previous")
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        },
        new("restore-previous-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Warm, RestoreVersion: "previous")
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        },
        new("restore-multiple-versions", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.MultipleVersions,
        },
        new("restore-local-conflict-no-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: false)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
        new("restore-local-conflict-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
        new("archive-no-pointers", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseNoPointers: true),
        new("archive-remove-local-then-thin-followup", ScenarioOperation.ArchiveThenRestore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseRemoveLocal: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
        new("archive-tier-planning", ScenarioOperation.Restore, ScenarioBackendRequirement.AzureArchiveCapable, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
    ];
}
