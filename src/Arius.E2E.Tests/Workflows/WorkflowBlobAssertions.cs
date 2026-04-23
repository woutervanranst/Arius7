using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal static class WorkflowBlobAssertions
{
    private const string DuplicateLargePathA = "archives/duplicates/binary-a.bin";
    private const string DuplicateLargePathB = "nested/deep/a/b/c/binary-b.bin";
    private const string DuplicateSmallPathA = "nested/deep/a/b/c/d/e/f/copy-b.bin";
    private const string DuplicateSmallPathB = "nested/deep/a/b/c/d/e/f/g/h/copy-c.bin";

    public static async Task<WorkflowRemoteStateSnapshot> AssertRemoteStateAsync(
        RepresentativeWorkflowState state,
        bool assertNoOpStability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var expectedSnapshot = state.CurrentMaterializedSnapshot
            ?? throw new InvalidOperationException("Current materialized snapshot is not available.");

        state.LatestSnapshotVersion.ShouldNotBeNullOrWhiteSpace("Latest snapshot version must be available before remote assertions.");
        state.LatestRootHash.ShouldNotBeNullOrWhiteSpace("Latest root hash must be available before remote assertions.");

        var snapshotBlobNames = await state.Fixture.Snapshot.ListBlobNamesAsync(cancellationToken);
        snapshotBlobNames.Count.ShouldBe(state.SnapshotCount, "Remote snapshot count should match the number of completed archive steps.");
        snapshotBlobNames.ShouldNotBeEmpty();
        Path.GetFileName(snapshotBlobNames[^1]).ShouldBe(state.LatestSnapshotVersion);

        var latestSnapshot = await state.Fixture.Snapshot.ResolveAsync(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Latest snapshot could not be resolved.");
        latestSnapshot.FileCount.ShouldBe(expectedSnapshot.Files.Count, "Latest snapshot file count should match the current materialized repository tree.");
        latestSnapshot.RootHash.ShouldBe(state.LatestRootHash, "Latest snapshot root hash should match the latest archive result.");

        var remoteState = await ReadRemoteStateAsync(state.Fixture.BlobContainer, latestSnapshot, cancellationToken);

        if (assertNoOpStability)
        {
            var baseline = state.NoOpArchiveBaseline
                ?? throw new InvalidOperationException("No-op archive baseline is not available.");

            remoteState.LatestRootHash.ShouldBe(baseline.RootHash, "No-op archive should preserve the root hash.");
            remoteState.ChunkCount.ShouldBe(baseline.ChunkCount, "No-op archive should not create additional chunk blobs.");
            remoteState.FileTreeCount.ShouldBe(baseline.FileTreeCount, "No-op archive should not create additional filetree blobs.");
        }

        await AssertLargeDuplicateLookupAsync(state, expectedSnapshot, cancellationToken);
        await AssertSmallFileTarLookupAsync(state, expectedSnapshot, cancellationToken);

        return remoteState;
    }

    static async Task<WorkflowRemoteStateSnapshot> ReadRemoteStateAsync(
        IBlobContainerService blobContainer,
        SnapshotManifest latestSnapshot,
        CancellationToken cancellationToken)
    {
        var chunkCount = await CountBlobsAsync(blobContainer, BlobPaths.Chunks, cancellationToken);
        var fileTreeCount = await CountBlobsAsync(blobContainer, BlobPaths.FileTrees, cancellationToken);

        return new WorkflowRemoteStateSnapshot(
            latestSnapshot.RootHash,
            chunkCount,
            fileTreeCount);
    }

    static async Task<int> CountBlobsAsync(
        IBlobContainerService blobContainer,
        string prefix,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in blobContainer.ListAsync(prefix, cancellationToken))
            count++;

        return count;
    }

    static async Task AssertLargeDuplicateLookupAsync(
        RepresentativeWorkflowState state,
        RepositoryTreeSnapshot expectedSnapshot,
        CancellationToken cancellationToken)
    {
        var contentHash = AssertDuplicateContentHash(expectedSnapshot, DuplicateLargePathA, DuplicateLargePathB);
        var entry = await state.Fixture.Index.LookupAsync(contentHash, cancellationToken);

        entry.ShouldNotBeNull($"Chunk index should resolve large duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldBe(contentHash, "Large duplicate files should resolve directly to a large chunk.");

        var metadata = await state.Fixture.BlobContainer.GetMetadataAsync(BlobPaths.Chunk(entry.ChunkHash), cancellationToken);
        metadata.Exists.ShouldBeTrue();
        metadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType).ShouldBeTrue();
        ariusType.ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    static async Task AssertSmallFileTarLookupAsync(
        RepresentativeWorkflowState state,
        RepositoryTreeSnapshot expectedSnapshot,
        CancellationToken cancellationToken)
    {
        var contentHash = AssertDuplicateContentHash(expectedSnapshot, DuplicateSmallPathA, DuplicateSmallPathB);
        var entry = await state.Fixture.Index.LookupAsync(contentHash, cancellationToken);

        entry.ShouldNotBeNull($"Chunk index should resolve small duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldNotBe(contentHash, "Small bundled files should resolve to their parent tar chunk hash.");

        var thinMetadata = await state.Fixture.BlobContainer.GetMetadataAsync(BlobPaths.Chunk(contentHash), cancellationToken);
        thinMetadata.Exists.ShouldBeTrue();
        thinMetadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var thinType).ShouldBeTrue();
        thinType.ShouldBe(BlobMetadataKeys.TypeThin);

        var tarMetadata = await state.Fixture.BlobContainer.GetMetadataAsync(BlobPaths.Chunk(entry.ChunkHash), cancellationToken);
        tarMetadata.Exists.ShouldBeTrue();
        tarMetadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var tarType).ShouldBeTrue();
        tarType.ShouldBe(BlobMetadataKeys.TypeTar);

        await using var thinStream = await state.Fixture.BlobContainer.DownloadAsync(BlobPaths.Chunk(contentHash), cancellationToken);
        using var reader = new StreamReader(thinStream);
        var parentChunkHash = await reader.ReadToEndAsync(cancellationToken);
        parentChunkHash.ShouldBe(entry.ChunkHash, "Thin chunk body should point at the tar chunk recorded in the chunk index.");
    }

    static string AssertDuplicateContentHash(
        RepositoryTreeSnapshot expectedSnapshot,
        string pathA,
        string pathB)
    {
        expectedSnapshot.Files.TryGetValue(pathA, out var hashA).ShouldBeTrue($"Expected repository snapshot to contain '{pathA}'.");
        expectedSnapshot.Files.TryGetValue(pathB, out var hashB).ShouldBeTrue($"Expected repository snapshot to contain '{pathB}'.");
        hashA.ShouldBe(hashB, $"Expected '{pathA}' and '{pathB}' to share the same content hash.");

        return hashA!;
    }
}

internal sealed record WorkflowRemoteStateSnapshot(
    string LatestRootHash,
    int ChunkCount,
    int FileTreeCount);
