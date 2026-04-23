using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Scenarios;

internal static class RepresentativeScenarioCatalog
{
    // First archive of the V1 dataset into an empty backend.
    internal static readonly RepresentativeScenarioDefinition InitialArchiveV1 =
        new("initial-archive-v1", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold);

    // Incremental archive after the backend already contains V1.
    internal static readonly RepresentativeScenarioDefinition IncrementalArchiveV2 =
        new("incremental-archive-v2", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.Incremental,
        };

    // Re-archive with no new content to confirm the no-op path.
    internal static readonly RepresentativeScenarioDefinition SecondArchiveNoChanges =
        new("second-archive-no-changes", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            ArchiveMode = ScenarioArchiveMode.NoChanges,
        };

    // Restore the latest snapshot with a cold local cache.
    internal static readonly RepresentativeScenarioDefinition RestoreLatestColdCache =
        new("restore-latest-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    // Restore the latest snapshot with a warm local cache.
    internal static readonly RepresentativeScenarioDefinition RestoreLatestWarmCache =
        new("restore-latest-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    // Restore the previous snapshot with a cold local cache.
    internal static readonly RepresentativeScenarioDefinition RestorePreviousColdCache =
        new("restore-previous-cold-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        };

    // Restore the previous snapshot with a warm local cache.
    internal static readonly RepresentativeScenarioDefinition RestorePreviousWarmCache =
        new("restore-previous-warm-cache", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.Previous,
        };

    // Restore both previous and latest snapshots in one representative flow.
    internal static readonly RepresentativeScenarioDefinition RestoreMultipleVersions =
        new("restore-multiple-versions", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Warm)
        {
            RestoreTarget = ScenarioRestoreTarget.MultipleVersions,
        };

    // Restore over conflicting local files without overwrite.
    internal static readonly RepresentativeScenarioDefinition RestoreLocalConflictNoOverwrite =
        new("restore-local-conflict-no-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: false)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    // Restore over conflicting local files with overwrite enabled.
    internal static readonly RepresentativeScenarioDefinition RestoreLocalConflictOverwrite =
        new("restore-local-conflict-overwrite", ScenarioOperation.Restore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V2, ScenarioCacheState.Cold, UseOverwrite: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    // Archive without creating pointer files on disk.
    internal static readonly RepresentativeScenarioDefinition ArchiveNoPointers =
        new("archive-no-pointers", ScenarioOperation.Archive, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseNoPointers: true);

    // Archive with remove-local, then verify a follow-up restore from thin chunks.
    internal static readonly RepresentativeScenarioDefinition ArchiveRemoveLocalThenThinFollowup =
        new("archive-remove-local-then-thin-followup", ScenarioOperation.ArchiveThenRestore, ScenarioBackendRequirement.Any, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold, UseRemoveLocal: true)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    // Plan and observe archive-tier restore behavior on Azure-capable storage.
    internal static readonly RepresentativeScenarioDefinition ArchiveTierPlanning =
        new("archive-tier-planning", ScenarioOperation.Restore, ScenarioBackendRequirement.AzureArchiveCapable, SyntheticRepositoryVersion.V1, ScenarioCacheState.Cold)
        {
            RestoreTarget = ScenarioRestoreTarget.Latest,
        };

    public static IReadOnlyList<RepresentativeScenarioDefinition> All { get; } =
    [
        InitialArchiveV1,
        IncrementalArchiveV2,
        SecondArchiveNoChanges,
        RestoreLatestColdCache,
        RestoreLatestWarmCache,
        RestorePreviousColdCache,
        RestorePreviousWarmCache,
        RestoreMultipleVersions,
        RestoreLocalConflictNoOverwrite,
        RestoreLocalConflictOverwrite,
        ArchiveNoPointers,
        ArchiveRemoveLocalThenThinFollowup,
        ArchiveTierPlanning,
    ];
}
