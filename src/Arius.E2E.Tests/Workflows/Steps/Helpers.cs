using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class Helpers
{

    public static Task<RestoreResult> RestoreAsync(E2EFixture fixture, bool overwrite, string? version, CancellationToken cancellationToken)
    {
        var options = new RestoreOptions
        {
            RootDirectory = fixture.RestoreRoot,
            Overwrite     = overwrite,
            Version       = version,
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
            var restoredPath = fixture.RestoreRoot / PathOf(conflictPath);
            var expectedConflictBytes = CreateConflictBytes(state.Seed, conflictPath);

            restoreResult.FilesSkipped.ShouldBeGreaterThan(0);
            (await restoredPath.ReadAllBytesAsync()).ShouldBe(expectedConflictBytes);
            return;
        }

        if (!state.VersionedSourceStates.TryGetValue(expectedVersion, out var expectedState))
            throw new InvalidOperationException($"Expected source state for version '{expectedVersion}' is not available.");

        await SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(expectedState, fixture.RestoreRoot, fixture.Encryption, includePointerFiles: false);

        if (!useNoPointers)
        {
            foreach (var relativePath in expectedState.Files.Keys)
            {
                var pointerPath = fixture.RestoreRoot / relativePath.ToPointerFilePath();

                pointerPath.ExistsFile.ShouldBeTrue($"Expected pointer file for {relativePath}");
            }
        }
    }

    public static async Task WriteRestoreConflictAsync(E2EFixture fixture, SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion, int seed)
    {
        var conflictPath = GetConflictPath(definition, expectedVersion);
        var fullPath = fixture.RestoreRoot / PathOf(conflictPath);
        if (fullPath.RelativePath.Parent is { } parent)
            (fixture.RestoreRoot / parent).CreateDirectory();

        var conflictBytes = CreateConflictBytes(seed, conflictPath);
        await fullPath.WriteAllBytesAsync(conflictBytes);
    }

    public static string? ResolveVersion(RepresentativeWorkflowState state, WorkflowRestoreTarget target) =>
        target switch
        {
            WorkflowRestoreTarget.Previous => state.PreviousSnapshotVersion ?? throw new InvalidOperationException("Previous snapshot version is not available."),
            _                              => null,
        };

    public static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken) 
        => await blobContainer.ListAsync(prefix, cancellationToken).CountAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveLatestSnapshotAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(cancellationToken: cancellationToken);

    public static Task<SnapshotManifest?> ResolveSnapshotByVersionAsync(RepresentativeWorkflowState state, string version, CancellationToken cancellationToken)
        => state.Fixture.Snapshot.ResolveAsync(version, cancellationToken);

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
        var thinBlobName = BlobPaths.ThinChunk(contentHash);

        entry.ShouldNotBeNull($"Chunk index should resolve small duplicate content hash '{contentHash}'.");
        entry!.ChunkHash.ShouldNotBe(ChunkHash.Parse(contentHash), "Small bundled files should resolve to their parent tar chunk hash.");

        // Assert that the ThinChunk is pointing to the correct TarChunk
        await using var thinStream = await state.Fixture.BlobContainer.DownloadAsync(thinBlobName, cancellationToken);
        using var reader = new StreamReader(thinStream);
        var parentChunkHash = ChunkHash.Parse(await reader.ReadToEndAsync(cancellationToken));
        parentChunkHash.ShouldBe(entry.ChunkHash, "Thin chunk body should point at the tar chunk recorded in the chunk index.");
    }

    static Task<ShardEntry?> LookupChunkAsync(RepresentativeWorkflowState state, ContentHash contentHash, CancellationToken cancellationToken)
        => state.Fixture.Index.LookupAsync(contentHash, cancellationToken);

    static string GetConflictPath(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion)
    {
        const string v1ChangedPath = "src/module-00/group-00/file-0000.bin";

        if (definition.Files.Any(file => file.Path == v1ChangedPath) && expectedVersion == SyntheticRepositoryVersion.V1)
            return v1ChangedPath;

        return definition.Files[0].Path;
    }

    static byte[] CreateConflictBytes(int seed, string path)
    {
        var bytes = new byte[1024];
        new Random(HashCode.Combine(seed, path, "restore-conflict")).NextBytes(bytes);
        return bytes;
    }

    static async Task<ContentHash> AssertDuplicateContentHashAsync(RepresentativeWorkflowState state, SyntheticRepositoryState expectedState, string pathA, string pathB, CancellationToken cancellationToken)
    {
        expectedState.Files.TryGetValue(PathOf(pathA), out var hashA).ShouldBeTrue($"Expected synthetic repository state to contain '{pathA}'.");
        expectedState.Files.TryGetValue(PathOf(pathB), out var hashB).ShouldBeTrue($"Expected synthetic repository state to contain '{pathB}'.");
        hashA.ShouldBe(hashB, $"Expected '{pathA}' and '{pathB}' to share the same content hash.");

        var contentHashA = await ComputeContentHashAsync(state, pathA, cancellationToken);
        var contentHashB = await ComputeContentHashAsync(state, pathB, cancellationToken);
        contentHashA.ShouldBe(contentHashB, $"Expected '{pathA}' and '{pathB}' to hash to the same content-addressed chunk.");

        return contentHashA;
    }

    static async Task<ContentHash> ComputeContentHashAsync(RepresentativeWorkflowState state, string relativePath, CancellationToken cancellationToken)
    {
        var             fullPath = E2EFixture.CombineValidatedRelativePath(state.Fixture.LocalRoot, PathOf(relativePath));
        await using var f        = fullPath.OpenRead();
        return await state.Fixture.Encryption.ComputeHashAsync(f, cancellationToken);
    }
}
