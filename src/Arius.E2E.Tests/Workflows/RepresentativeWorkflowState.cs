using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Scenarios;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext Context { get; init; }

    public required Func<E2EStorageBackendContext, CancellationToken, Task<E2EFixture>> CreateFixtureAsync { get; init; }

    public required E2EFixture Fixture { get; set; }

    public required SyntheticRepositoryDefinition Definition { get; init; }

    public required int Seed { get; init; }

    public SyntheticRepositoryVersion? CurrentSourceVersion { get; set; }

    public RepositoryTreeSnapshot? CurrentMaterializedSnapshot { get; set; }

    public string? PreviousSnapshotVersion { get; set; }

    public string? LatestSnapshotVersion { get; set; }

    public string? LatestRootHash { get; set; }

    public int SnapshotCount { get; set; }

    public WorkflowNoOpArchiveBaseline? NoOpArchiveBaseline { get; set; }

    public ArchiveTierWorkflowOutcome? ArchiveTierOutcome { get; set; }
}

internal sealed record WorkflowNoOpArchiveBaseline(
    string RootHash,
    int ChunkCount,
    int FileTreeCount);
