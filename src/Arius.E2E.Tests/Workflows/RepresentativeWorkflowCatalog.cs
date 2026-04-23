using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    // First archive of the V1 dataset into an empty backend.
    internal static readonly RepresentativeWorkflowDefinition InitialArchiveV1 =
        new("initial-archive-v1", RepresentativeWorkflowOperation.Archive, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Cold));

    // Incremental archive after the backend already contains V1.
    internal static readonly RepresentativeWorkflowDefinition IncrementalArchiveV2 =
        new("incremental-archive-v2", RepresentativeWorkflowOperation.Archive, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Warm))
        {
            ArchiveMode = RepresentativeWorkflowArchiveMode.Incremental,
        };

    // Re-archive with no new content to confirm the no-op path.
    internal static readonly RepresentativeWorkflowDefinition SecondArchiveNoChanges =
        new("second-archive-no-changes", RepresentativeWorkflowOperation.Archive, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Warm))
        {
            ArchiveMode = RepresentativeWorkflowArchiveMode.NoChanges,
        };

    // Restore the latest snapshot with a cold local cache.
    internal static readonly RepresentativeWorkflowDefinition RestoreLatestColdCache =
        new("restore-latest-cold-cache", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Cold))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    // Restore the latest snapshot with a warm local cache.
    internal static readonly RepresentativeWorkflowDefinition RestoreLatestWarmCache =
        new("restore-latest-warm-cache", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Warm))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    // Restore the previous snapshot with a cold local cache.
    internal static readonly RepresentativeWorkflowDefinition RestorePreviousColdCache =
        new("restore-previous-cold-cache", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Cold))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Previous,
        };

    // Restore the previous snapshot with a warm local cache.
    internal static readonly RepresentativeWorkflowDefinition RestorePreviousWarmCache =
        new("restore-previous-warm-cache", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Warm))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Previous,
        };

    // Restore both previous and latest snapshots in one representative flow.
    internal static readonly RepresentativeWorkflowDefinition RestoreMultipleVersions =
        new("restore-multiple-versions", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Warm))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.MultipleVersions,
        };

    // Restore over conflicting local files without overwrite.
    internal static readonly RepresentativeWorkflowDefinition RestoreLocalConflictNoOverwrite =
        new("restore-local-conflict-no-overwrite", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Cold), UseOverwrite: false)
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    // Restore over conflicting local files with overwrite enabled.
    internal static readonly RepresentativeWorkflowDefinition RestoreLocalConflictOverwrite =
        new("restore-local-conflict-overwrite", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V2, RepresentativeWorkflowCacheState.Cold), UseOverwrite: true)
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    // Archive without creating pointer files on disk.
    internal static readonly RepresentativeWorkflowDefinition ArchiveNoPointers =
        new("archive-no-pointers", RepresentativeWorkflowOperation.Archive, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Cold), UseNoPointers: true);

    // Archive with remove-local, then verify a follow-up restore from thin chunks.
    internal static readonly RepresentativeWorkflowDefinition ArchiveRemoveLocalThenThinFollowup =
        new("archive-remove-local-then-thin-followup", RepresentativeWorkflowOperation.ArchiveThenRestore, RepresentativeWorkflowBackendRequirement.Any, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Cold), UseRemoveLocal: true)
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    // Plan and observe archive-tier restore behavior on Azure-capable storage.
    internal static readonly RepresentativeWorkflowDefinition ArchiveTierPlanning =
        new("archive-tier-planning", RepresentativeWorkflowOperation.Restore, RepresentativeWorkflowBackendRequirement.AzureArchiveCapable, new RepresentativeWorkflowState(SyntheticRepositoryVersion.V1, RepresentativeWorkflowCacheState.Cold))
        {
            RestoreTarget = RepresentativeWorkflowRestoreTarget.Latest,
        };

    public static IReadOnlyList<RepresentativeWorkflowDefinition> All { get; } =
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
