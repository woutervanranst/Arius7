using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class Helpers
{

    public static Task<RestoreResult> RestoreAsync(E2EFixture fixture, bool overwrite, string? version, CancellationToken cancellationToken)
    {
        var options = new RestoreOptions
        {
            RootDirectory = fixture.RestoreDirectory.ToString(),
            Overwrite = overwrite,
            Version = version,
        };

        return fixture.CreateRestoreHandler().Handle(new RestoreCommand(options), cancellationToken).AsTask();
    }

    public static async Task AssertRestoreOutcomeAsync(
        E2EFixture fixture,
        RepresentativeWorkflowState state,
        SyntheticRepositoryVersion expectedVersion,
        bool useNoPointers,
        RestoreResult restoreResult,
        bool preserveConflictBytes)
    {
        if (preserveConflictBytes)
        {
            var conflictPath = GetConflictPath(state.Definition, expectedVersion);
            var expectedConflictBytes = CreateConflictBytes(state.Seed, conflictPath);

            restoreResult.FilesSkipped.ShouldBeGreaterThan(0);
            (await fixture.RestoreFileSystem.ReadAllBytesAsync(conflictPath, CancellationToken.None)).ShouldBe(expectedConflictBytes);
            return;
        }

        if (!state.VersionedSourceStates.TryGetValue(expectedVersion, out var expectedState))
            throw new InvalidOperationException($"Expected source state for version '{expectedVersion}' is not available.");

        await SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(expectedState, fixture.RestoreDirectory, fixture.Encryption, includePointerFiles: false);

        if (!useNoPointers)
        {
            foreach (var relativePath in expectedState.Files.Keys)
                fixture.RestoreFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue($"Expected pointer file for {relativePath}");
        }
    }

    public static async Task WriteRestoreConflictAsync(E2EFixture fixture, SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion, int seed)
    {
        var conflictPath = GetConflictPath(definition, expectedVersion);

        var conflictBytes = CreateConflictBytes(seed, conflictPath);
        await fixture.RestoreFileSystem.WriteAllBytesAsync(conflictPath, conflictBytes, CancellationToken.None);
    }

    public static string? ResolveVersion(RepresentativeWorkflowState state, WorkflowRestoreTarget target) =>
        target switch
        {
            WorkflowRestoreTarget.Previous => state.PreviousSnapshotVersion ?? throw new InvalidOperationException("Previous snapshot version is not available."),
            _                              => null,
        };

    public static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, RelativePath prefix, CancellationToken cancellationToken)
        => await blobContainer.ListAsync(prefix, cancellationToken: cancellationToken).CountAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveLatestSnapshotAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => state.Fixture.Repository.Snapshot.ResolveAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveSnapshotByVersionAsync(RepresentativeWorkflowState state, string version, CancellationToken cancellationToken)
        => state.Fixture.Repository.Snapshot.ResolveAsync(version, cancellationToken);

    public static async Task AssertLargeDuplicateLookupAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, CancellationToken cancellationToken)
    {
        var contentHash = await AssertDuplicateContentHashAsync(state, expectedState, SyntheticRepositoryDefinitionFactory.LargeDuplicatePathA, SyntheticRepositoryDefinitionFactory.LargeDuplicatePathB, cancellationToken);
        var entry       = await LookupChunkAsync(state, contentHash, cancellationToken);

        entry.ShouldNotBeNull($"Chunk index should resolve large duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldBe(ChunkHash.Parse(contentHash), "Large duplicate files should resolve directly to a large chunk.");
    }

    public static async Task AssertSmallFileTarLookupAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, CancellationToken cancellationToken)
    {
        var contentHash  = await AssertDuplicateContentHashAsync(state, expectedState, SyntheticRepositoryDefinitionFactory.SmallDuplicateStablePathA, SyntheticRepositoryDefinitionFactory.SmallDuplicateStablePathB, cancellationToken);
        var entry        = await LookupChunkAsync(state, contentHash, cancellationToken);
        var thinBlobName = BlobPaths.ThinChunkPath(contentHash);

        entry.ShouldNotBeNull($"Chunk index should resolve small duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldNotBe(ChunkHash.Parse(contentHash), "Small bundled files should resolve to their parent tar chunk hash.");

        // Assert that the thin chunk metadata points to the correct tar chunk.
        var thinMetadata = await state.Fixture.BlobContainer.GetMetadataAsync(thinBlobName, cancellationToken);
        thinMetadata.Metadata.ShouldContainKey(BlobMetadataKeys.ParentChunkHash);
        var parentChunkHash = ChunkHash.Parse(thinMetadata.Metadata[BlobMetadataKeys.ParentChunkHash]);
        parentChunkHash.ShouldBe(entry.ChunkHash, "Thin chunk metadata should point at the tar chunk recorded in the chunk index.");
    }

    private static Task<ShardEntry?> LookupChunkAsync(RepresentativeWorkflowState state, ContentHash contentHash, CancellationToken cancellationToken)
        => state.Fixture.Repository.Index.LookupAsync(contentHash, cancellationToken);

    private static RelativePath GetConflictPath(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion)
    {
        var v1ChangedPath = RelativePath.Parse("src/module-00/group-00/file-0000.bin");

        if (definition.Files.Any(file => file.Path == v1ChangedPath) && expectedVersion == SyntheticRepositoryVersion.V1)
            return v1ChangedPath;

        return definition.Files[0].Path;
    }

    private static byte[] CreateConflictBytes(int seed, RelativePath path)
    {
        var bytes = new byte[1024];
        new Random(HashCode.Combine(seed, path, "restore-conflict")).NextBytes(bytes);
        return bytes;
    }

    private static async Task<ContentHash> AssertDuplicateContentHashAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, RelativePath pathA, RelativePath pathB, CancellationToken cancellationToken)
    {
        expectedState.Files.TryGetValue(pathA, out var hashA).ShouldBeTrue($"Expected synthetic repository state to contain '{pathA}'.");
        expectedState.Files.TryGetValue(pathB, out var hashB).ShouldBeTrue($"Expected synthetic repository state to contain '{pathB}'.");
        hashA.ShouldBe(hashB, $"Expected '{pathA}' and '{pathB}' to share the same content hash.");

        var contentHashA = await ComputeContentHashAsync(state, pathA, cancellationToken);
        var contentHashB = await ComputeContentHashAsync(state, pathB, cancellationToken);
        contentHashA.ShouldBe(contentHashB, $"Expected '{pathA}' and '{pathB}' to hash to the same content-addressed chunk.");

        return contentHashA;
    }

    private static async Task<ContentHash> ComputeContentHashAsync(RepresentativeWorkflowState state, RelativePath relativePath, CancellationToken cancellationToken)
    {
        await using var f = state.Fixture.LocalFileSystem.OpenRead(relativePath);
        return await state.Fixture.Encryption.ComputeHashAsync(f, cancellationToken);
    }
}
