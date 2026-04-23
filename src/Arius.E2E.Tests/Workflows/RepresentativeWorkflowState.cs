using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Scenarios;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext BackendContext { get; init; }

    public IRepresentativeScenarioFixture? Fixture { get; set; }

    public required RepresentativeWorkflowDefinition Definition { get; init; }

    public required int Seed { get; init; }

    public SyntheticRepositoryVersion CurrentSourceVersion { get; set; }

    public RepositoryTreeSnapshot? CurrentMaterializedSnapshot { get; set; }

    public string? PreviousSnapshotVersion { get; set; }

    public string? LatestSnapshotVersion { get; set; }

    public ArchiveTierWorkflowOutcome? ArchiveTierOutcome { get; set; }
}
