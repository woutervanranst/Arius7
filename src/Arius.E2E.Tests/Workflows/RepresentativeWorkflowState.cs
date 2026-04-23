using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext Context { get; init; }

    public required E2EFixture Fixture { get; init; }

    public required SyntheticRepositoryDefinition Definition { get; init; }

    public required int Seed { get; init; }

    public SyntheticRepositoryVersion? CurrentSourceVersion { get; set; }

    public RepositoryTreeSnapshot? CurrentMaterializedSnapshot { get; set; }

    public string? PreviousSnapshotVersion { get; set; }

    public string? LatestSnapshotVersion { get; set; }

    public ArchiveTierWorkflowOutcome? ArchiveTierOutcome { get; set; }
}
