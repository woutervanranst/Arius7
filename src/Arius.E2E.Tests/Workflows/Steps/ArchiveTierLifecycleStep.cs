using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Tests.Shared.IO;
using Arius.Tests.Shared;

namespace Arius.E2E.Tests.Workflows.Steps;

/// <summary>
/// Exercises the Azure archive-tier lifecycle for one representative tar-backed target by
/// 1. preserving the existing readable chunk blob,
/// 2. moving that chunk into archive tier,
/// 3. verifying the pending rehydration path, then
/// 4. restoring successfully from a ready rehydrated blob plus cleanup.
/// </summary>
internal sealed record ArchiveTierLifecycleStep(string Name, RelativePath TargetPath) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (!state.Context.Capabilities.SupportsArchiveTier)
            return;

        var azureBlobContainer = state.Context.AzureBlobContainerService;
        azureBlobContainer.ShouldNotBeNull($"{Name}: archive-tier workflow requires Azure blob storage.");

        var sourceVersion = state.CurrentSourceVersion
            ?? throw new InvalidOperationException($"{Name}: current source version is not available.");

        await state.Fixture.PreserveLocalCacheAsync();
        await state.Fixture.DisposeAsync();
        state.Fixture = await state.CreateFixtureAsync(state.Context, cancellationToken);

        if (!state.VersionedSourceStates.TryGetValue(sourceVersion, out var sourceState))
            throw new InvalidOperationException($"{Name}: source state for version '{sourceVersion}' is not available.");

        var versionedSourceFileSystem = new RelativeFileSystem(state.VersionedSourceDirectory);
        if (sourceVersion == SyntheticRepositoryVersion.V2 && !versionedSourceFileSystem.DirectoryExists(sourceState.RootDirectory))
        {
            var v1State = await MaterializeVersionStep.RematerializeV1Async(state, cancellationToken);
            var versionRootPath = LocalDirectory.Parse(state.VersionedSourceDirectory.Resolve(RelativePath.Parse(nameof(SyntheticRepositoryVersion.V2))));
            sourceState = await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(state.Definition, state.Seed, v1State.RootDirectory, versionRootPath, state.Fixture.Encryption);
            state.VersionedSourceStates[SyntheticRepositoryVersion.V2] = sourceState;
        }

        // 1. Reuse the existing archived source content from the canonical workflow.
        FileSystemHelper.CopyDirectory(sourceState.RootDirectory, state.Fixture.LocalDirectory);

        // 2. Pick one representative tar-backed file under the target subtree and preserve the
        // exact existing chunk blob so we can later stage it as a ready rehydrated blob.
        var targetChunk = await IdentifyTargetTarChunkAsync(state.Fixture, TargetPath, cancellationToken);

        // 3. Force that existing chunk into archive tier.
        await MoveChunksToArchiveAsync(azureBlobContainer, targetChunk.ChunkHash, cancellationToken);

        // 4. First restore run: verify that archive-tier restore prompts for rehydration and
        // does not restore the chosen target while the chunk is still archived.
        var firstEstimateCaptured = false;
        var initialResult = await state.Fixture.CreateRestoreHandler()
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreDirectory.ToString(),
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

        var pendingRehydratedBlobCount = await Helpers.CountBlobsAsync(azureBlobContainer, BlobPaths.ChunksRehydratedPrefix, cancellationToken);
        pendingRehydratedBlobCount.ShouldBeGreaterThan(0, $"{Name}: pending restore should stage rehydrated chunk blobs.");

        // 5. Replace the pending staged blob with the preserved readable blob so the next restore
        // observes the post-rehydration path without waiting on Azure's real archive-tier timing.
        await DeleteBlobsAsync(azureBlobContainer, BlobPaths.ChunksRehydratedPrefix, cancellationToken);
        await UploadReadyRehydratedChunkAsync(azureBlobContainer, targetChunk, cancellationToken);

        var cleanupDeletedChunks = 0;
        var readyRestoreDirectory = state.WorkflowDirectory / RelativePath.Parse("archive-tier-ready");
        var readyRestoreFileSystem = new RelativeFileSystem(readyRestoreDirectory);
        readyRestoreFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            // 6. Second restore run: verify that restore now succeeds from chunks-rehydrated/
            // and that it cleans up the temporary rehydrated blob afterward.
            var readyResult = await state.Fixture.CreateRestoreHandler().Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = readyRestoreDirectory.ToString(),
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
                readyRestoreFileSystem);

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
            readyRestoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }

        static async Task<ArchiveTierTargetChunk> IdentifyTargetTarChunkAsync(E2EFixture fixture, RelativePath targetPath, CancellationToken cancellationToken)
        {
            // Select one representative tar-backed file under the subtree and preserve the exact
            // existing chunk blob bytes/metadata so the ready path can reuse the real blob.
            foreach (var file in fixture.LocalFileSystem.EnumerateFiles(targetPath, SearchOption.AllDirectories))
            {
                var relativePath = file.Path;
                var bytes       = await fixture.LocalFileSystem.ReadAllBytesAsync(relativePath, cancellationToken); // todo use streaming
                var contentHash = fixture.Encryption.ComputeHash(bytes);
                var entry       = await fixture.Repository.Index.LookupAsync(contentHash, cancellationToken);

                entry.ShouldNotBeNull($"Expected chunk index entry for '{relativePath}'.");
                if (entry!.IsLargeChunk)
                    continue;

                var             chunkBlobName  = BlobPaths.ChunkPath(entry.ChunkHash);
                await using var chunkStream    = await fixture.BlobContainer.DownloadAsync(chunkBlobName, cancellationToken);
                using var       preservedChunk = new MemoryStream();
                await chunkStream.CopyToAsync(preservedChunk, cancellationToken);

                var metadata = await fixture.BlobContainer.GetMetadataAsync(chunkBlobName, cancellationToken);
                return new ArchiveTierTargetChunk(relativePath, contentHash, entry.ChunkHash, preservedChunk.ToArray(), metadata.Metadata);
            }

            throw new InvalidOperationException($"Expected at least one tar chunk under '{targetPath}'.");
        }

        static async Task MoveChunksToArchiveAsync(AzureBlobContainerService blobContainer, ChunkHash chunkHash, CancellationToken cancellationToken)
        {
            var blobName = BlobPaths.ChunkPath(chunkHash);
            await blobContainer.SetTierAsync(blobName, BlobTier.Archive, cancellationToken);
        }

        static Task UploadReadyRehydratedChunkAsync(AzureBlobContainerService blobContainer, ArchiveTierTargetChunk targetChunk, CancellationToken cancellationToken)
        {
            var rehydratedBlobName = BlobPaths.ChunkRehydratedPath(targetChunk.ChunkHash);

            return blobContainer.UploadAsync(rehydratedBlobName, new MemoryStream(targetChunk.PreservedChunkBytes), targetChunk.Metadata, BlobTier.Hot, overwrite: true, cancellationToken: cancellationToken);
        }

        static async Task DeleteBlobsAsync(IBlobContainerService blobContainer, RelativePath prefix, CancellationToken cancellationToken)
        {
            var blobNames = new List<RelativePath>();

            await foreach (var blobName in blobContainer.ListAsync(prefix, cancellationToken))
                blobNames.Add(blobName);

            foreach (var blobName in blobNames)
                await blobContainer.DeleteAsync(blobName, cancellationToken);
        }

        static async Task AssertArchiveTierRestoreOutcomeAsync(
            ArchiveTierTargetChunk targetChunk,
            IEncryptionService encryption,
            RelativeFileSystem readyRestoreFileSystem)
        {
            readyRestoreFileSystem.FileExists(targetChunk.TargetRelativePath).ShouldBeTrue($"Expected restored file for {targetChunk.TargetRelativePath}");

            await using var stream = readyRestoreFileSystem.OpenRead(targetChunk.TargetRelativePath);
            var restoredHash = await encryption.ComputeHashAsync(stream);
            restoredHash.ShouldBe(targetChunk.ContentHash, $"Expected restored content for {targetChunk.TargetRelativePath}");

            readyRestoreFileSystem.FileExists(targetChunk.TargetRelativePath.ToPointerPath()).ShouldBeTrue($"Expected pointer file for {targetChunk.TargetRelativePath}");
        }
    }

    sealed record ArchiveTierTargetChunk(RelativePath TargetRelativePath, ContentHash ContentHash, ChunkHash ChunkHash, byte[] PreservedChunkBytes, IReadOnlyDictionary<string, string> Metadata);
}
