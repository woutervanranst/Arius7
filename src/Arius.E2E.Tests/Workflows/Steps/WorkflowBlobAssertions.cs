using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class WorkflowBlobAssertions
{
    private const string DuplicateLargePathA = "archives/duplicates/binary-a.bin";
    private const string DuplicateLargePathB = "nested/deep/a/b/c/binary-b.bin";
    private const string DuplicateSmallPathA = "nested/deep/a/b/c/d/e/f/copy-b.bin";
    private const string DuplicateSmallPathB = "nested/deep/a/b/c/d/e/f/g/h/copy-c.bin";

    public static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in blobContainer.ListAsync(prefix, cancellationToken))
            count++;

        return count;
    }

    public static Task<SnapshotManifest?> ResolveLatestAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveVersionAsync(RepresentativeWorkflowState state, string version, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(version, cancellationToken);

    static Task<ShardEntry?> LookupChunkAsync(RepresentativeWorkflowState state, string contentHash, CancellationToken cancellationToken)
        => state.Fixture.Index.LookupAsync(contentHash, cancellationToken);

    public static async Task AssertLargeDuplicateLookupAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, CancellationToken cancellationToken)
    {
        var contentHash = await AssertDuplicateContentHashAsync(state, expectedState, DuplicateLargePathA, DuplicateLargePathB, cancellationToken);
        var entry       = await LookupChunkAsync(state, contentHash, cancellationToken);
        var metadata    = await state.Fixture.BlobContainer.GetMetadataAsync(BlobPaths.Chunk(contentHash), cancellationToken);

        entry.ShouldNotBeNull($"Chunk index should resolve large duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldBe(contentHash, "Large duplicate files should resolve directly to a large chunk.");

        metadata.Exists.ShouldBeTrue();
        metadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType).ShouldBeTrue();
        ariusType.ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    public static async Task AssertSmallFileTarLookupAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, CancellationToken cancellationToken)
    {
        var contentHash  = await AssertDuplicateContentHashAsync(state, expectedState, DuplicateSmallPathA, DuplicateSmallPathB, cancellationToken);
        var entry        = await LookupChunkAsync(state, contentHash, cancellationToken);
        var thinBlobName = BlobPaths.Chunk(contentHash);

        entry.ShouldNotBeNull($"Chunk index should resolve small duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldNotBe(contentHash, "Small bundled files should resolve to their parent tar chunk hash.");

        var thinMetadata = await state.Fixture.BlobContainer.GetMetadataAsync(thinBlobName, cancellationToken);
        thinMetadata.Exists.ShouldBeTrue();
        thinMetadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var thinType).ShouldBeTrue();
        thinType.ShouldBe(BlobMetadataKeys.TypeThin);

        await using var thinStream = await state.Fixture.BlobContainer.DownloadAsync(thinBlobName, cancellationToken);
        using var reader = new StreamReader(thinStream);
        var parentChunkHash = await reader.ReadToEndAsync(cancellationToken);
        parentChunkHash.ShouldBe(entry.ChunkHash, "Thin chunk body should point at the tar chunk recorded in the chunk index.");

        var tarMetadata = await state.Fixture.BlobContainer.GetMetadataAsync(BlobPaths.Chunk(parentChunkHash), cancellationToken);
        tarMetadata.Exists.ShouldBeTrue();
        tarMetadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var tarType).ShouldBeTrue();
        tarType.ShouldBe(BlobMetadataKeys.TypeTar);
    }

    static async Task<string> AssertDuplicateContentHashAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, string pathA, string pathB, CancellationToken cancellationToken)
    {
        expectedState.Files.TryGetValue(pathA, out var hashA).ShouldBeTrue($"Expected synthetic repository state to contain '{pathA}'.");
        expectedState.Files.TryGetValue(pathB, out var hashB).ShouldBeTrue($"Expected synthetic repository state to contain '{pathB}'.");
        hashA.ShouldBe(hashB, $"Expected '{pathA}' and '{pathB}' to share the same content hash.");

        var contentHashA = await ComputeContentHashAsync(state, pathA, cancellationToken);
        var contentHashB = await ComputeContentHashAsync(state, pathB, cancellationToken);
        contentHashA.ShouldBe(contentHashB, $"Expected '{pathA}' and '{pathB}' to hash to the same content-addressed chunk.");

        return contentHashA;
    }

    static async Task<string> ComputeContentHashAsync(RepresentativeWorkflowState state, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = E2EFixture.CombineValidatedRelativePath(state.Fixture.LocalRoot, relativePath);
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return Convert.ToHexString(state.Fixture.Encryption.ComputeHash(bytes)).ToLowerInvariant();
    }
}
