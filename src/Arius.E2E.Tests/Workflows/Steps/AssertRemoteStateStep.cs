using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal enum RemoteAssertionKind
{
    InitialArchive,
    IncrementalArchive,
    NoOpArchive,
}

internal sealed record AssertRemoteStateStep(
    string Name,
    RemoteAssertionKind Kind,
    bool CaptureNoOpPreCounts = false) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var latest = await WorkflowBlobAssertions.ResolveLatestAsync(state, cancellationToken);
        latest.ShouldNotBeNull($"{Name}: latest snapshot should exist.");

        var expectedSnapshot = state.CurrentMaterializedSnapshot
            ?? throw new InvalidOperationException($"{Name}: current materialized snapshot is not available.");

        state.LatestSnapshotVersion.ShouldNotBeNullOrWhiteSpace($"{Name}: latest snapshot version should be available.");
        Path.GetFileName((await state.Fixture.Snapshot.ListBlobNamesAsync(cancellationToken))[^1])
            .ShouldBe(state.LatestSnapshotVersion, $"{Name}: latest resolved snapshot should match the most recent archive result.");

        switch (Kind)
        {
            case RemoteAssertionKind.InitialArchive:
                (await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken))
                    .ShouldBe(1, $"{Name}: initial archive should create one snapshot.");
                latest.FileCount.ShouldBe(expectedSnapshot.Files.Count, $"{Name}: latest snapshot file count should match the current materialized repository tree.");
                break;

            case RemoteAssertionKind.IncrementalArchive:
                (await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken))
                    .ShouldBe(2, $"{Name}: incremental archive should create a second snapshot.");
                latest.FileCount.ShouldBe(expectedSnapshot.Files.Count, $"{Name}: latest snapshot file count should match the current materialized repository tree.");
                await WorkflowBlobAssertions.AssertLargeDuplicateLookupAsync(state, expectedSnapshot, cancellationToken);
                await WorkflowBlobAssertions.AssertSmallFileTarLookupAsync(state, expectedSnapshot, cancellationToken);
                break;

            case RemoteAssertionKind.NoOpArchive:
                state.PreviousSnapshotVersion.ShouldNotBeNullOrWhiteSpace($"{Name}: previous snapshot version should be available.");
                var previous = await WorkflowBlobAssertions.ResolveVersionAsync(state, state.PreviousSnapshotVersion, cancellationToken);
                previous.ShouldNotBeNull($"{Name}: previous snapshot should exist.");
                latest.RootHash.ShouldBe(previous.RootHash, $"{Name}: no-op archive should preserve the root hash.");

                var chunkCount = await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Chunks, cancellationToken);
                var fileTreeCount = await WorkflowBlobAssertions.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.FileTrees, cancellationToken);

                chunkCount.ShouldBe(state.ChunkBlobCountBeforeNoOpArchive, $"{Name}: no-op archive should not create additional chunk blobs.");
                fileTreeCount.ShouldBe(state.FileTreeBlobCountBeforeNoOpArchive, $"{Name}: no-op archive should not create additional filetree blobs.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (!CaptureNoOpPreCounts)
            return;

        state.ChunkBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
            state.Context.BlobContainer,
            BlobPaths.Chunks,
            cancellationToken);
        state.FileTreeBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
            state.Context.BlobContainer,
            BlobPaths.FileTrees,
            cancellationToken);
    }
}
