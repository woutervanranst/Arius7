using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

internal static class RepresentativeScenarioCatalog
{
    public static IReadOnlyList<RepresentativeScenarioDefinition> All { get; } =
    [
        // First archive of the V1 dataset into an empty backend.
        new("initial-archive-v1", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold),

        // Incremental archive after the backend already contains V1.
        new("incremental-archive-v2", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.Incremental,
        },

        // Re-archive with no new content to confirm the no-op path.
        new("second-archive-no-changes", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.NoChanges,
        },

        // Restore the latest snapshot with a cold local cache.
        new("restore-latest-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },

        // Restore the latest snapshot with a warm local cache.
        new("restore-latest-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },

        // Restore the previous snapshot with a cold local cache.
        new("restore-previous-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, RestoreVersion: "previous")
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        },

        // Restore the previous snapshot with a warm local cache.
        new("restore-previous-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Warm, RestoreVersion: "previous")
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        },

        // Restore both previous and latest snapshots in one representative flow.
        new("restore-multiple-versions", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.MultipleVersions,
        },

        // Restore over conflicting local files without overwrite.
        new("restore-local-conflict-no-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: false)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },

        // Restore over conflicting local files with overwrite enabled.
        new("restore-local-conflict-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },

        // Archive without creating pointer files on disk.
        new("archive-no-pointers", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseNoPointers: true),

        // Archive with remove-local, then verify a follow-up restore from thin chunks.
        new("archive-remove-local-then-thin-followup", ScenarioOperation.ArchiveThenRestore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseRemoveLocal: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },

        // Plan and observe archive-tier restore behavior on Azure-capable storage.
        new("archive-tier-planning", ScenarioOperation.Restore, ScenarioBackendRequirement.AzureArchiveCapable, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        },
    ];
}
