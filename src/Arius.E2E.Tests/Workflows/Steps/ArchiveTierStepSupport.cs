using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class ArchiveTierStepSupport
{
    public static Task<RestoreCommandHandler> CreateRestoreHandlerAsync(
        E2EFixture fixture,
        E2EStorageBackendContext context,
        IBlobContainerService blobContainer)
    {
        return Task.FromResult(new RestoreCommandHandler(
            fixture.Encryption,
            fixture.Index,
            new ChunkStorageService(blobContainer, fixture.Encryption),
            new FileTreeService(blobContainer, fixture.Encryption, fixture.Index, context.AccountName, context.ContainerName),
            new SnapshotService(blobContainer, fixture.Encryption, context.AccountName, context.ContainerName),
            Substitute.For<IMediator>(),
            new FakeLogger<RestoreCommandHandler>(),
            context.AccountName,
            context.ContainerName));
    }

    internal sealed record ArchiveTierTarChunk(
        string ChunkHash,
        IReadOnlyDictionary<string, byte[]> ContentHashToBytes);

    public static async Task<IReadOnlyList<ArchiveTierTarChunk>> IdentifyTarChunksAsync(
        E2EFixture fixture,
        string targetPath,
        CancellationToken cancellationToken)
    {
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

    public static async Task MoveChunksToArchiveAsync(
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

    public static async Task SideloadRehydratedTarChunkAsync(
        AzureBlobContainerService blobContainer,
        IEncryptionService encryption,
        string tarChunkHash,
        IReadOnlyDictionary<string, byte[]> contentHashToBytes,
        CancellationToken cancellationToken)
    {
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

    public static async Task DeleteBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
    {
        var blobNames = new List<string>();

        await foreach (var blobName in blobContainer.ListAsync(prefix, cancellationToken))
            blobNames.Add(blobName);

        foreach (var blobName in blobNames)
            await blobContainer.DeleteAsync(blobName, cancellationToken);
    }

    public static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
    {
        var count = 0;

        await foreach (var _ in blobContainer.ListAsync(prefix, cancellationToken))
            count++;

        return count;
    }

    public static async Task AssertRestoreOutcomeAsync(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion sourceVersion,
        int seed,
        string targetPath,
        string readyRestoreRoot)
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-expected-{Guid.NewGuid():N}");
        try
        {
            var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(definition, sourceVersion, seed, expectedRoot);

            var expectedRestoreState = FilterSyntheticRepositoryStateToPrefix(expected, targetPath, trimPrefix: false);

            await SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(
                expectedRestoreState,
                readyRestoreRoot,
                includePointerFiles: false);

            foreach (var relativePath in expectedRestoreState.Files.Keys)
            {
                var pointerPath = Path.Combine(readyRestoreRoot, (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {relativePath}");
            }
        }
        finally
        {
            if (Directory.Exists(expectedRoot))
                Directory.Delete(expectedRoot, recursive: true);
        }
    }

    static SyntheticRepositoryState FilterSyntheticRepositoryStateToPrefix(
        SyntheticRepositoryState state,
        string prefix,
        bool trimPrefix)
    {
        var normalizedPrefix = prefix.TrimEnd('/') + "/";

        return new SyntheticRepositoryState(state.Files
            .Where(pair => pair.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => trimPrefix ? pair.Key[normalizedPrefix.Length..] : pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }
}
