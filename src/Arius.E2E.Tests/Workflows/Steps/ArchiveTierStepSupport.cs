using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
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

    public static async Task<string?> PollForArchiveTierTarChunkAsync(AzureBlobContainerService blobContainer, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);

        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await foreach (var blobName in blobContainer.ListAsync(BlobPaths.Chunks, cancellationToken))
            {
                var metadata = await blobContainer.GetMetadataAsync(blobName, cancellationToken);
                if (metadata.Tier != BlobTier.Archive)
                    continue;

                if (metadata.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType) &&
                    ariusType == BlobMetadataKeys.TypeTar)
                {
                    return blobName[BlobPaths.Chunks.Length..];
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    public static async Task<Dictionary<string, byte[]>> ReadContentBytesAsync(string localRoot, string targetPath)
    {
        var contentHashToBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(
                     Path.Combine(localRoot, targetPath.Replace('/', Path.DirectorySeparatorChar)),
                     "*",
                     SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            contentHashToBytes[Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()] = bytes;
        }

        return contentHashToBytes;
    }

    public static async Task SideloadRehydratedTarChunkAsync(
        AzureBlobContainerService blobContainer,
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
        await using (var gzip = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true))
        {
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
