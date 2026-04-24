using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
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

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowRunnerDependencies
{
    public Func<E2EStorageBackendContext, CancellationToken, Task<E2EFixture>> CreateFixtureAsync { get; init; } =
        async (context, cancellationToken) => await RepresentativeWorkflowRunner.CreateFixtureAsync(context, cancellationToken);
}

internal static class RepresentativeWorkflowRunner
{
    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, CancellationToken cancellationToken)
    {
        return await E2EFixture.CreateAsync(context.BlobContainer, context.AccountName, context.ContainerName, BlobTier.Cool, cancellationToken: cancellationToken);
    }

    public static async Task<RepresentativeWorkflowRunResult> RunAsync(
        IE2EStorageBackend backend,
        RepresentativeWorkflowDefinition workflow,
        RepresentativeWorkflowRunnerDependencies? dependencies = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(workflow);
        dependencies ??= new RepresentativeWorkflowRunnerDependencies();

        await using var context = await backend.CreateContextAsync(cancellationToken);
        var fixture = await dependencies.CreateFixtureAsync(context, cancellationToken);
        RepresentativeWorkflowState? state = null;

        try
        {
            state = new RepresentativeWorkflowState
            {
                Context            = context,
                CreateFixtureAsync = dependencies.CreateFixtureAsync,
                Fixture            = fixture,
                Definition         = SyntheticRepositoryDefinitionFactory.Create(workflow.Profile),
                Seed               = workflow.Seed,
            };

            foreach (var step in workflow.Steps)
                await step.ExecuteAsync(state, cancellationToken);

            return new RepresentativeWorkflowRunResult(false, ArchiveTierOutcome: state.ArchiveTierOutcome);
        }
        finally
        {
            if (state is not null)
                await state.Fixture.DisposeAsync();
            else
                await fixture.DisposeAsync();
        }
    }

    internal static Task<ArchiveResult> ArchiveAsync(E2EFixture fixture, ArchiveCommandOptions options, CancellationToken cancellationToken = default)
    {
        return fixture.CreateArchiveHandler().Handle(new ArchiveCommand(options), cancellationToken).AsTask();
    }

    internal static Task<RestoreResult> RestoreAsync(E2EFixture fixture, RestoreOptions options, CancellationToken cancellationToken = default)
    {
        return fixture.CreateRestoreHandler().Handle(new RestoreCommand(options), cancellationToken).AsTask();
    }

    internal static ArchiveCommandOptions CreateArchiveOptions(E2EFixture fixture, bool useNoPointers = false, bool useRemoveLocal = false, BlobTier uploadTier = BlobTier.Cool)
    {
        return new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = uploadTier,
            NoPointers = useNoPointers,
            RemoveLocal = useRemoveLocal,
        };
    }

    internal static ArchiveCommandOptions CreateArchiveTierOptions(E2EFixture fixture)
    {
        return new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = BlobTier.Archive,
        };
    }

    internal static async Task AssertRestoreOutcomeAsync(E2EFixture fixture, SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion, int seed, bool useNoPointers, RestoreResult restoreResult, bool preserveConflictBytes)
    {
        if (preserveConflictBytes)
        {
            var conflictPath = GetConflictPath(definition, expectedVersion);
            var restoredPath = Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', Path.DirectorySeparatorChar));
            var expectedConflictBytes = CreateConflictBytes(seed, conflictPath);

            restoreResult.FilesSkipped.ShouldBeGreaterThan(0);
            (await File.ReadAllBytesAsync(restoredPath)).ShouldBe(expectedConflictBytes);
            return;
        }

        var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-expected-{Guid.NewGuid():N}");
        try
        {
            var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                expectedVersion,
                seed,
                expectedRoot);

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(expected, fixture.RestoreRoot, includePointerFiles: false);

            if (!useNoPointers)
            {
                foreach (var relativePath in expected.Files.Keys)
                {
                    var pointerPath = Path.Combine(
                        fixture.RestoreRoot,
                        (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                    File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {relativePath}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(expectedRoot))
                Directory.Delete(expectedRoot, recursive: true);
        }
    }

    internal static async Task WriteRestoreConflictAsync(E2EFixture fixture, SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion, int seed)
    {
        var conflictPath = GetConflictPath(definition, expectedVersion);
        var fullPath = Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var conflictBytes = CreateConflictBytes(seed, conflictPath);
        await File.WriteAllBytesAsync(fullPath, conflictBytes);
    }

    internal static async Task AssertArchiveTierRestoreOutcomeAsync(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion sourceVersion, int seed, string targetPath, string readyRestoreRoot)
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-expected-{Guid.NewGuid():N}");
        try
        {
            var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(definition, sourceVersion, seed, expectedRoot);

            var expectedRestoreTree = FilterSnapshotToPrefix(expected, targetPath, trimPrefix: false);

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(expectedRestoreTree, readyRestoreRoot, includePointerFiles: false);

            foreach (var relativePath in expectedRestoreTree.Files.Keys)
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

    internal static string FormatSnapshotVersion(DateTimeOffset snapshotTime) => snapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);

    internal static string GetConflictPath(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion)
    {
        const string v1ChangedPath = "src/module-00/group-00/file-0000.bin";

        if (definition.Files.Any(file => file.Path == v1ChangedPath) && expectedVersion == SyntheticRepositoryVersion.V1)
        {
            return v1ChangedPath;
        }

        return definition.Files[0].Path;
    }

    internal static byte[] CreateConflictBytes(int seed, string path)
    {
        var bytes = new byte[1024];
        new Random(HashCode.Combine(seed, path, "restore-conflict")).NextBytes(bytes);
        return bytes;
    }

    internal static RestoreCommandHandler CreateArchiveTierRestoreHandler(E2EFixture fixture, E2EStorageBackendContext context, IBlobContainerService blobContainer)
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

    internal static async Task<string?> PollForArchiveTierTarChunkAsync(AzureBlobContainerService blobContainer, CancellationToken cancellationToken)
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

    internal static async Task<Dictionary<string, byte[]>> ReadArchiveTierContentBytesAsync(string localRoot, string targetPath)
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

    internal static async Task SideloadRehydratedTarChunkAsync(AzureBlobContainerService blobContainer, string tarChunkHash, IReadOnlyDictionary<string, byte[]> contentHashToBytes, CancellationToken cancellationToken)
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

    internal static async Task<int> CountBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
    {
        var count = 0;

        await foreach (var _ in blobContainer.ListAsync(prefix, cancellationToken))
            count++;

        return count;
    }

    internal static async Task DeleteBlobsAsync(IBlobContainerService blobContainer, string prefix, CancellationToken cancellationToken)
    {
        var blobNames = new List<string>();

        await foreach (var blobName in blobContainer.ListAsync(prefix, cancellationToken))
            blobNames.Add(blobName);

        foreach (var blobName in blobNames)
            await blobContainer.DeleteAsync(blobName, cancellationToken);
    }

    static RepositoryTreeSnapshot FilterSnapshotToPrefix(RepositoryTreeSnapshot snapshot, string prefix, bool trimPrefix)
    {
        var normalizedPrefix = prefix.TrimEnd('/') + "/";

        return new RepositoryTreeSnapshot(snapshot.Files
            .Where(pair => pair.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => trimPrefix ? pair.Key[normalizedPrefix.Length..] : pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }
}
