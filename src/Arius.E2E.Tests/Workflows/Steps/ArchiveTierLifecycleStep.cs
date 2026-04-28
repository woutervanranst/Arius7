using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Tests.Shared.IO;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.E2E.Tests.Workflows.Steps;

/// <summary>
/// Exercises the Azure archive-tier lifecycle for one representative tar-backed target by
/// 1. preserving the existing readable chunk blob,
/// 2. moving that chunk into archive tier,
/// 3. verifying the pending rehydration path, then
/// 4. restoring successfully from a ready rehydrated blob plus cleanup.
/// </summary>
internal sealed record ArchiveTierLifecycleStep(string Name, string TargetPath = "src") : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (!state.Context.Capabilities.SupportsArchiveTier)
            return;

        var azureBlobContainer = state.Context.AzureBlobContainerService;
        azureBlobContainer.ShouldNotBeNull($"{Name}: archive-tier workflow requires Azure blob storage.");

        var sourceVersion = state.CurrentSourceVersion
            ?? throw new InvalidOperationException($"{Name}: current source version is not available.");

        await state.Fixture.DisposeAsync();
        state.Fixture = await state.CreateFixtureAsync(state.Context, cancellationToken);

        if (!state.VersionedSourceStates.TryGetValue(sourceVersion, out var sourceState))
            throw new InvalidOperationException($"{Name}: source state for version '{sourceVersion}' is not available.");

        if (!Directory.Exists(sourceState.RootPath) && sourceVersion == SyntheticRepositoryVersion.V2)
        {
            var v1State = await MaterializeVersionStep.RematerializeV1Async(state, cancellationToken);
            var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V2));
            sourceState = await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(state.Definition, state.Seed, v1State.RootPath, versionRootPath, state.Fixture.Encryption);
            state.VersionedSourceStates[SyntheticRepositoryVersion.V2] = sourceState;
        }

        // 1. Reuse the existing archived source content from the canonical workflow.
        FileSystemHelper.CopyDirectory(sourceState.RootPath, state.Fixture.LocalRoot);

        // 2. Pick one representative tar-backed file under the target subtree and preserve the
        // exact existing chunk blob so we can later stage it as a ready rehydrated blob.
        var targetChunk = await IdentifyTargetTarChunkAsync(state.Fixture, TargetPath, cancellationToken);

        // 3. Force that existing chunk into archive tier.
        await MoveChunksToArchiveAsync(azureBlobContainer, targetChunk.ChunkHash, cancellationToken);

        // 4. First restore run: verify that archive-tier restore prompts for rehydration and
        // does not restore the chosen target while the chunk is still archived.
        var firstEstimateCaptured = false;
        var initialRestoreHandler = CreateArchiveTierRestoreHandler(state.Fixture, state.Context, azureBlobContainer);
        var initialResult = await initialRestoreHandler
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = targetChunk.TargetRelativePath,
                Overwrite = true,
                ConfirmRehydration = (estimate, _) =>
                {
                    firstEstimateCaptured = true;
                    (estimate.ChunksNeedingRehydration + estimate.ChunksPendingRehydration)
                        .ShouldBeGreaterThan(0, $"{Name}: pending archive-tier restore should request rehydration.");
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }), cancellationToken).AsTask();

        initialResult.Success.ShouldBeTrue($"{Name}: pending restore failed: {initialResult.ErrorMessage}");
        initialResult.ChunksPendingRehydration.ShouldBeGreaterThan(0, $"{Name}: pending restore should report pending chunks.");
        initialResult.FilesRestored.ShouldBe(0, $"{Name}: pending restore should not restore files before rehydration is ready.");

        var pendingRehydratedBlobCount = await CountBlobsAsync(azureBlobContainer, BlobPaths.ChunksRehydrated, cancellationToken);
        pendingRehydratedBlobCount.ShouldBeGreaterThan(0, $"{Name}: pending restore should stage rehydrated chunk blobs.");

        // 5. Replace the pending staged blob with the preserved readable blob so the next restore
        // observes the post-rehydration path without waiting on Azure's real archive-tier timing.
        await DeleteBlobsAsync(azureBlobContainer, BlobPaths.ChunksRehydrated, cancellationToken);
        await UploadReadyRehydratedChunkAsync(azureBlobContainer, targetChunk, cancellationToken);

        var cleanupDeletedChunks = 0;
        var workflowRoot = Path.GetDirectoryName(state.VersionedSourceRoot)
            ?? throw new InvalidOperationException($"{Name}: representative workflow root is not available.");
        var readyRestoreRoot = Path.Combine(workflowRoot, "archive-tier-ready");
        Directory.CreateDirectory(readyRestoreRoot);

        try
        {
            // 6. Second restore run: verify that restore now succeeds from chunks-rehydrated/
            // and that it cleans up the temporary rehydrated blob afterward.
            var readyResult = await state.Fixture.CreateRestoreHandler().Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = readyRestoreRoot,
                TargetPath = targetChunk.TargetRelativePath,
                Overwrite = true,
                ConfirmCleanup = (count, _, _) =>
                {
                    cleanupDeletedChunks = count;
                    return Task.FromResult(true);
                },
            }), cancellationToken).AsTask();

            readyResult.Success.ShouldBeTrue($"{Name}: ready restore failed: {readyResult.ErrorMessage}");
            readyResult.ChunksPendingRehydration.ShouldBe(0, $"{Name}: ready restore should not leave pending rehydration chunks.");

            await AssertArchiveTierRestoreOutcomeAsync(
                targetChunk,
                state.Fixture.Encryption,
                readyRestoreRoot);

            cleanupDeletedChunks.ShouldBeGreaterThan(0, $"{Name}: ready restore should clean up rehydrated tar chunks.");

            state.ArchiveTierOutcome = new ArchiveTierWorkflowOutcome(
                firstEstimateCaptured,
                initialResult.ChunksPendingRehydration,
                initialResult.FilesRestored,
                0,
                0,
                readyResult.FilesRestored,
                readyResult.ChunksPendingRehydration,
                cleanupDeletedChunks,
                pendingRehydratedBlobCount);
        }
        finally
        {
            if (Directory.Exists(readyRestoreRoot))
                Directory.Delete(readyRestoreRoot, recursive: true);
        }

        static RestoreCommandHandler CreateArchiveTierRestoreHandler(E2EFixture fixture, E2EStorageBackendContext context, IBlobContainerService blobContainer)
        {
            return new RestoreCommandHandler(
                fixture.Encryption,
                fixture.Index,
                new ChunkStorageService(blobContainer, fixture.Encryption),
                new FileTreeService(blobContainer, fixture.Encryption, fixture.Index, context.AccountName, context.ContainerName),
                new SnapshotService(blobContainer, fixture.Encryption, context.AccountName, context.ContainerName),
                Substitute.For<IMediator>(),
                new FakeLogger<RestoreCommandHandler>(),
                context.AccountName,
                context.ContainerName);
        }

        static async Task<ArchiveTierTargetChunk> IdentifyTargetTarChunkAsync(E2EFixture fixture, string targetPath, CancellationToken cancellationToken)
        {
            // Select one representative tar-backed file under the subtree and preserve the exact
            // existing chunk blob bytes/metadata so the ready path can reuse the real blob.
            var targetRoot = E2EFixture.CombineValidatedRelativePath(fixture.LocalRoot, targetPath);

            foreach (var filePath in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
            {
                var bytes       = await File.ReadAllBytesAsync(filePath, cancellationToken); // todo use streaming
                var contentHash = fixture.Encryption.ComputeHash(bytes);
                var entry       = await fixture.Index.LookupAsync(contentHash, cancellationToken);

                entry.ShouldNotBeNull($"Expected chunk index entry for '{filePath}'.");
                if (entry!.IsLargeChunk)
                    continue;

                var             chunkBlobName  = BlobPaths.Chunk(entry.ChunkHash);
                await using var chunkStream    = await fixture.BlobContainer.DownloadAsync(chunkBlobName, cancellationToken);
                using var       preservedChunk = new MemoryStream();
                await chunkStream.CopyToAsync(preservedChunk, cancellationToken);

                var metadata     = await fixture.BlobContainer.GetMetadataAsync(chunkBlobName, cancellationToken);
                var relativePath = Path.GetRelativePath(fixture.LocalRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');

                return new ArchiveTierTargetChunk(relativePath, contentHash, entry.ChunkHash, preservedChunk.ToArray(), metadata.Metadata);
            }

            throw new InvalidOperationException($"Expected at least one tar chunk under '{targetPath}'.");
        }

        static async Task MoveChunksToArchiveAsync(AzureBlobContainerService blobContainer, ChunkHash chunkHash, CancellationToken cancellationToken)
        {
            var blobName = BlobPaths.Chunk(chunkHash);
            await blobContainer.SetTierAsync(blobName, BlobTier.Archive, cancellationToken);
        }

        static Task UploadReadyRehydratedChunkAsync(AzureBlobContainerService blobContainer, ArchiveTierTargetChunk targetChunk, CancellationToken cancellationToken)
        {
            var rehydratedBlobName = BlobPaths.ChunkRehydrated(targetChunk.ChunkHash);

            return blobContainer.UploadAsync(rehydratedBlobName, new MemoryStream(targetChunk.PreservedChunkBytes), targetChunk.Metadata, BlobTier.Hot, overwrite: true, cancellationToken: cancellationToken);
        }

        static async Task DeleteBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
        {
            var blobNames = new List<string>();

            await foreach (var blobName in blobContainer.ListAsync(prefix, cancellationToken))
                blobNames.Add(blobName);

            foreach (var blobName in blobNames)
                await blobContainer.DeleteAsync(blobName, cancellationToken);
        }

        static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken) 
            => await blobContainer.ListAsync(prefix, cancellationToken).CountAsync(cancellationToken: cancellationToken);

        static async Task AssertArchiveTierRestoreOutcomeAsync(ArchiveTierTargetChunk targetChunk, IEncryptionService encryption, string readyRestoreRoot)
        {
            var restoredPath = Path.Combine(readyRestoreRoot, targetChunk.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(restoredPath).ShouldBeTrue($"Expected restored file for {targetChunk.TargetRelativePath}");

            await using var stream = File.OpenRead(restoredPath);
            var restoredHash = await encryption.ComputeHashAsync(stream);
            restoredHash.ShouldBe(targetChunk.ContentHash, $"Expected restored content for {targetChunk.TargetRelativePath}");

            var pointerPath = Path.Combine(readyRestoreRoot, (targetChunk.TargetRelativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));
            File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {targetChunk.TargetRelativePath}");
        }
    }

    sealed record ArchiveTierTargetChunk(string TargetRelativePath, ContentHash ContentHash, ChunkHash ChunkHash, byte[] PreservedChunkBytes, IReadOnlyDictionary<string, string> Metadata);
}
