using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal enum RemoteAssertionKind
{
    InitialArchive,
    IncrementalArchive,
    NoOpArchive,
}

internal sealed record AssertRemoteStateStep(string Name, RemoteAssertionKind Kind) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var latest = await Helpers.ResolveLatestAsync(state, cancellationToken);
        latest.ShouldNotBeNull($"{Name}: latest snapshot should exist.");

        var expectedState = state.CurrentSyntheticRepositoryState
            ?? throw new InvalidOperationException($"{Name}: current synthetic repository state is not available.");

        state.LatestSnapshotVersion.ShouldNotBeNullOrWhiteSpace($"{Name}: latest snapshot version should be available.");
        Path.GetFileName((await state.Fixture.Snapshot.ListBlobNamesAsync(cancellationToken))[^1])
            .ShouldBe(state.LatestSnapshotVersion, $"{Name}: latest resolved snapshot should match the most recent archive result.");

        switch (Kind)
        {
            case RemoteAssertionKind.InitialArchive:
                (await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken))
                    .ShouldBe(1, $"{Name}: initial archive should create one snapshot.");
                latest.FileCount.ShouldBe(expectedState.Files.Count, $"{Name}: latest snapshot file count should match the current synthetic dataset state.");
                break;

            case RemoteAssertionKind.IncrementalArchive:
                (await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Snapshots, cancellationToken))
                    .ShouldBe(2, $"{Name}: incremental archive should create a second snapshot.");
                latest.FileCount.ShouldBe(expectedState.Files.Count, $"{Name}: latest snapshot file count should match the current synthetic dataset state.");
                await Helpers.AssertLargeDuplicateLookupAsync(state, expectedState, cancellationToken);
                await Helpers.AssertSmallFileTarLookupAsync(state, expectedState, cancellationToken);
                break;

            case RemoteAssertionKind.NoOpArchive:
                state.PreviousSnapshotVersion.ShouldNotBeNullOrWhiteSpace($"{Name}: previous snapshot version should be available.");
                var previous = await Helpers.ResolveVersionAsync(state, state.PreviousSnapshotVersion, cancellationToken);
                previous.ShouldNotBeNull($"{Name}: previous snapshot should exist.");
                latest.RootHash.ShouldBe(previous.RootHash, $"{Name}: no-op archive should preserve the root hash.");

                var chunkCount = await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Chunks, cancellationToken);
                var fileTreeCount = await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.FileTrees, cancellationToken);

                chunkCount.ShouldBe(
                    state.ChunkBlobCountBeforeNoOpArchive ?? throw new InvalidOperationException($"{Name}: pre-no-op chunk blob count was not captured."),
                    $"{Name}: no-op archive should not create additional chunk blobs.");
                fileTreeCount.ShouldBe(
                    state.FileTreeBlobCountBeforeNoOpArchive ?? throw new InvalidOperationException($"{Name}: pre-no-op filetree blob count was not captured."),
                    $"{Name}: no-op archive should not create additional filetree blobs.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }
}
