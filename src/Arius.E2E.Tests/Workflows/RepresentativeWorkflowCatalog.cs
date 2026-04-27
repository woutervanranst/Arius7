using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    internal static readonly RepresentativeWorkflowDefinition Canonical =
        new(
            "canonical-representative-workflow",
            SyntheticRepositoryProfile.Representative,
            20010523,
            [
                new MaterializeVersionStep(SyntheticRepositoryVersion.V1),
                new ArchiveStep("archive-v1"),
                new AssertRemoteStateStep("assert-initial-archive", RemoteAssertionKind.InitialArchive),
                new RestoreStep("restore-latest-v1", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V1),

                new MaterializeVersionStep(SyntheticRepositoryVersion.V2),
                new ArchiveStep("archive-v2"),
                new AssertRemoteStateStep("assert-incremental-archive", RemoteAssertionKind.IncrementalArchive),
                new RestoreStep("restore-latest-v2-warm", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),

                new ResetCacheStep(),
                new RestoreStep("restore-latest-v2-cold", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),
                new RestoreStep("restore-previous-v1", WorkflowRestoreTarget.Previous, SyntheticRepositoryVersion.V1),

                new MaterializeVersionStep(SyntheticRepositoryVersion.V2),
                new ArchiveStep("archive-v2-noop", CaptureNoOpPreCounts: true),
                new AssertRemoteStateStep("assert-noop-archive", RemoteAssertionKind.NoOpArchive),

                new ArchiveStep("archive-no-pointers", NoPointers: true),
                new RestoreStep("restore-no-pointers", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2, ExpectPointers: false),

                new ArchiveStep("archive-remove-local", RemoveLocal: true),
                new RestoreStep("restore-after-remove-local", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2),

                new AssertConflictBehaviorStep("restore-conflict-no-overwrite", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2, Overwrite: false),
                new AssertConflictBehaviorStep("restore-conflict-overwrite", WorkflowRestoreTarget.Latest, SyntheticRepositoryVersion.V2, Overwrite: true),

                new MaterializeVersionStep(SyntheticRepositoryVersion.V2),
                new ArchiveTierLifecycleStep("archive-tier-lifecycle", "src"),
            ]);
}
