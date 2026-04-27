using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext                                            Context                            { get; init; }
    public required Func<E2EStorageBackendContext, CancellationToken, Task<E2EFixture>> CreateFixtureAsync                 { get; init; }
    public required E2EFixture                                                          Fixture                            { get; set; }
    public required SyntheticRepositoryDefinition                                       Definition                         { get; init; }
    public required int                                                                 Seed                               { get; init; }
    public required string                                                              VersionedSourceRoot                { get; init; }
    public          SyntheticRepositoryVersion?                                         CurrentSourceVersion               { get; set; }
    public          SyntheticRepositoryState?                                           CurrentSyntheticRepositoryState    { get; set; }
    public          Dictionary<SyntheticRepositoryVersion, SyntheticRepositoryState>    VersionedSourceStates              { get; } = new();
    public          string?                                                             PreviousSnapshotVersion            { get; set; }
    public          string?                                                             LatestSnapshotVersion              { get; set; }
    public          int?                                                                ChunkBlobCountBeforeNoOpArchive    { get; set; }
    public          int?                                                                FileTreeBlobCountBeforeNoOpArchive { get; set; }
    public          string?                                                             SnapshotVersionBeforeNoOpArchive   { get; set; }
    public          bool?                                                               NoOpArchivePreservedSnapshot       { get; set; }
    public          ArchiveTierWorkflowOutcome?                                         ArchiveTierOutcome                 { get; set; }
}
