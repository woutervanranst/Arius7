using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Services;
using Arius.Tests.Shared.IO;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using System.Formats.Tar;
using System.IO.Compression;

namespace Arius.E2E.Tests.Workflows.Steps;

/// <summary>
/// Exercises the Azure archive-tier lifecycle for one source subtree by
/// 1. forcing its tar chunks into archive tier
/// 2. verifying the pending rehydration path, then
/// 3. sideloading ready rehydrated chunks and
/// 4. verifying the final restore plus cleanup.
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

        // Start from a clean fixture rooted at the preserved versioned source tree so the
        // archive-tier checks run against the same content the workflow archived earlier.
        FileSystemHelper.CopyDirectory(sourceState.RootPath, state.Fixture.LocalRoot);

        // Identify the tar chunks backing the target subtree and move those existing chunks
        // to archive tier. The workflow reuses the canonical history instead of re-archiving.
        var tarChunks = await IdentifyTarChunksAsync(state.Fixture, TargetPath, cancellationToken);
        await MoveChunksToArchiveAsync(
            azureBlobContainer,
            tarChunks.Select(chunk => chunk.ChunkHash),
            cancellationToken);

        // First restore pass should detect archived chunks, request rehydration, and avoid
        // restoring files until the rehydrated chunk blobs become available.
        var firstEstimateCaptured = false;
        var firstTrackingBlobService = new CopyTrackingBlobService(azureBlobContainer);
        var initialRestoreHandler = CreateArchiveTierRestoreHandler(state.Fixture, state.Context, firstTrackingBlobService);
        var initialResult = await initialRestoreHandler
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
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

        var pendingRehydratedBlobCount = await CountBlobsAsync(
            azureBlobContainer,
            BlobPaths.ChunksRehydrated,
            cancellationToken);
        pendingRehydratedBlobCount.ShouldBeGreaterThan(0, $"{Name}: pending restore should stage rehydrated chunk blobs.");

        var rerunTrackingBlobService = new CopyTrackingBlobService(azureBlobContainer);
        var rerunRestoreHandler = CreateArchiveTierRestoreHandler(state.Fixture, state.Context, rerunTrackingBlobService);
        var rerunResult = await rerunRestoreHandler
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            }), cancellationToken).AsTask();

        rerunResult.Success.ShouldBeTrue($"{Name}: pending rerun failed: {rerunResult.ErrorMessage}");
        rerunTrackingBlobService.CopyCalls.Count.ShouldBe(0, $"{Name}: rerun should not issue duplicate rehydration copy requests.");

        // Replace the pending rehydrated blobs with ready blobs so the next restore observes
        // the post-rehydration path without waiting on Azure's real archive-tier timing.
        await DeleteBlobsAsync(
            azureBlobContainer,
            BlobPaths.ChunksRehydrated,
            cancellationToken);

        foreach (var tarChunk in tarChunks)
        {
            await SideloadRehydratedTarChunkAsync(
                azureBlobContainer,
                state.Fixture.Encryption,
                tarChunk.ChunkHash,
                tarChunk.ContentHashToBytes,
                cancellationToken);
        }

        var cleanupDeletedChunks = 0;
        var workflowRoot = Path.GetDirectoryName(state.VersionedSourceRoot)
            ?? throw new InvalidOperationException($"{Name}: representative workflow root is not available.");
        var readyRestoreRoot = Path.Combine(workflowRoot, "archive-tier-ready");
        Directory.CreateDirectory(readyRestoreRoot);

        try
        {
            // Ready restore should now succeed, consume the rehydrated tar chunks, and clean
            // up the temporary rehydrated blobs after the target subtree is restored.
            var readyResult = await state.Fixture.CreateRestoreHandler().Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = readyRestoreRoot,
                TargetPath = TargetPath,
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
                sourceState,
                state.Fixture.Encryption,
                TargetPath,
                readyRestoreRoot);

            cleanupDeletedChunks.ShouldBeGreaterThan(0, $"{Name}: ready restore should clean up rehydrated tar chunks.");

            state.ArchiveTierOutcome = new ArchiveTierWorkflowOutcome(
                firstEstimateCaptured,
                initialResult.ChunksPendingRehydration,
                initialResult.FilesRestored,
                rerunResult.ChunksPendingRehydration,
                rerunTrackingBlobService.CopyCalls.Count,
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

        static RestoreCommandHandler CreateArchiveTierRestoreHandler(
            E2EFixture fixture,
            E2EStorageBackendContext context,
            IBlobContainerService blobContainer)
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

        static async Task<IReadOnlyList<ArchiveTierTarChunk>> IdentifyTarChunksAsync(
            E2EFixture fixture,
            string targetPath,
            CancellationToken cancellationToken)
        {
            // Map each content hash under the target subtree back to its tar chunk so the step
            // can archive and later sideload exactly the chunks needed for this restore path.
            var targetRoot = E2EFixture.CombineValidatedRelativePath(fixture.LocalRoot, targetPath);
            var contentByChunkHash = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

            foreach (var filePath in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                var contentHash = Convert.ToHexString(fixture.Encryption.ComputeHash(bytes)).ToLowerInvariant();
                var entry = await fixture.Index.LookupAsync(contentHash, cancellationToken);

                entry.ShouldNotBeNull($"Expected chunk index entry for '{filePath}'.");
                if (entry!.ChunkHash == contentHash)
                    continue;

                if (!contentByChunkHash.TryGetValue(entry.ChunkHash, out var chunkContents))
                {
                    chunkContents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    contentByChunkHash[entry.ChunkHash] = chunkContents;
                }

                chunkContents[contentHash] = bytes;
            }

            contentByChunkHash.Count.ShouldBeGreaterThan(0, $"Expected at least one tar chunk under '{targetPath}'.");

            return contentByChunkHash
                .Select(pair => new ArchiveTierTarChunk(pair.Key, pair.Value))
                .ToArray();
        }

        static async Task MoveChunksToArchiveAsync(
            AzureBlobContainerService blobContainer,
            IEnumerable<string> chunkHashes,
            CancellationToken cancellationToken)
        {
            foreach (var chunkHash in chunkHashes.Distinct(StringComparer.Ordinal))
            {
                var blobName = BlobPaths.Chunk(chunkHash);
                await blobContainer.SetTierAsync(blobName, BlobTier.Archive, cancellationToken);

                var metadata = await blobContainer.GetMetadataAsync(blobName, cancellationToken);
                metadata.Tier.ShouldBe(BlobTier.Archive, $"Expected '{blobName}' to be moved to archive tier.");
                metadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType).ShouldBeTrue();
                ariusType.ShouldBe(BlobMetadataKeys.TypeTar, $"Expected '{blobName}' to be a tar chunk.");
            }
        }

        static async Task SideloadRehydratedTarChunkAsync(
            AzureBlobContainerService blobContainer,
            IEncryptionService encryption,
            string tarChunkHash,
            IReadOnlyDictionary<string, byte[]> contentHashToBytes,
            CancellationToken cancellationToken)
        {
            // Rebuild the rehydrated tar chunk in the same encrypted on-disk format Arius uses
            // so the ready restore path exercises the real chunk reader and cleanup logic.
            var rehydratedBlobName = BlobPaths.ChunkRehydrated(tarChunkHash);
            var rehydratedMeta = await blobContainer.GetMetadataAsync(rehydratedBlobName, cancellationToken);
            if (rehydratedMeta.Exists && rehydratedMeta.Tier == BlobTier.Archive)
                await blobContainer.DeleteAsync(rehydratedBlobName, cancellationToken);

            var sourceMeta = await blobContainer.GetMetadataAsync(BlobPaths.Chunk(tarChunkHash), cancellationToken);

            using var memoryStream = new MemoryStream();
            await using (var encryptionStream = encryption.WrapForEncryption(memoryStream))
            {
                await using var gzip = new GZipStream(encryptionStream, CompressionLevel.Optimal, leaveOpen: true);
                await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
                foreach (var (contentHash, rawBytes) in contentHashToBytes)
                {
                    var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, contentHash)
                    {
                        DataStream = new MemoryStream(rawBytes),
                    };

                    await tar.WriteEntryAsync(tarEntry, cancellationToken);
                }
            }

            memoryStream.Position = 0;
            await blobContainer.UploadAsync(rehydratedBlobName, memoryStream, sourceMeta.Metadata, BlobTier.Hot, overwrite: true, cancellationToken: cancellationToken);
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
        {
            var count = 0;

            await foreach (var _ in blobContainer.ListAsync(prefix, cancellationToken))
                count++;

            return count;
        }

        static async Task AssertArchiveTierRestoreOutcomeAsync(
            SyntheticRepositoryState sourceState,
            IEncryptionService encryption,
            string targetPath,
            string readyRestoreRoot)
        {
            var expectedRestoreState = FilterSyntheticRepositoryStateToPrefix(sourceState, targetPath, trimPrefix: false);

            await SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(
                expectedRestoreState,
                readyRestoreRoot,
                encryption,
                includePointerFiles: false);

            foreach (var relativePath in expectedRestoreState.Files.Keys)
            {
                var pointerPath = Path.Combine(readyRestoreRoot, (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {relativePath}");
            }
        }

        static SyntheticRepositoryState FilterSyntheticRepositoryStateToPrefix(
            SyntheticRepositoryState state,
            string prefix,
            bool trimPrefix)
        {
            var normalizedPrefix = prefix.TrimEnd('/') + "/";

            return new SyntheticRepositoryState(state.RootPath, state.Files
                .Where(pair => pair.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                .ToDictionary(
                    pair => trimPrefix ? pair.Key[normalizedPrefix.Length..] : pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal));
        }
    }

    sealed record ArchiveTierTarChunk(string ChunkHash, IReadOnlyDictionary<string, byte[]> ContentHashToBytes);
}
