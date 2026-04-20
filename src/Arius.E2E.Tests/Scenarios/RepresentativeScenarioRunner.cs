using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Services;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.E2E.Tests.Scenarios;

internal interface IRepresentativeScenarioFixture : IAsyncDisposable
{
    string LocalRoot { get; }

    string RestoreRoot { get; }

    Task PreserveLocalCacheAsync();

    Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion version,
        int seed);

    Task<ArchiveResult> ArchiveAsync(ArchiveCommandOptions options, CancellationToken ct = default);

    Task<RestoreResult> RestoreAsync(RestoreOptions options, CancellationToken ct = default);
}

internal sealed class RepresentativeScenarioRunnerDependencies
{
    public Func<E2EStorageBackendContext, CancellationToken, Task<IRepresentativeScenarioFixture>> CreateFixtureAsync { get; init; } =
        async (context, cancellationToken) => await RepresentativeScenarioRunner.CreateFixtureAsync(context, cancellationToken);

    public Func<string, string, Task> ResetLocalCacheAsync { get; init; } = E2EFixture.ResetLocalCacheAsync;

    public bool AssertRestoreTrees { get; init; }
}

internal sealed record RepresentativeScenarioRunResult(
    bool WasSkipped,
    string? SkipReason = null,
    ArchiveTierScenarioOutcome? ArchiveTierOutcome = null);

internal sealed record ArchiveTierScenarioOutcome(
    bool WasCostEstimateCaptured,
    int InitialPendingChunks,
    int InitialFilesRestored,
    int PendingChunksOnRerun,
    int RerunCopyCalls,
    int ReadyFilesRestored,
    int ReadyPendingChunks,
    int CleanupDeletedChunks);

internal sealed record RestoreExecutionPlan(
    RestoreOptions Options,
    SyntheticRepositoryVersion ExpectedVersion);

internal static class RepresentativeScenarioRunner
{
    internal static async Task<IRepresentativeScenarioFixture> CreateFixtureAsync(
        E2EStorageBackendContext context,
        CancellationToken cancellationToken)
    {
        var fixture = await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            ct: cancellationToken);

        return new E2EScenarioFixtureAdapter(fixture);
    }

    public static async Task<RepresentativeScenarioRunResult> RunAsync(
        IE2EStorageBackend backend,
        RepresentativeScenarioDefinition scenario,
        SyntheticRepositoryProfile profile,
        int seed,
        RepresentativeScenarioRunnerDependencies? dependencies = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(scenario);
        dependencies ??= new RepresentativeScenarioRunnerDependencies();

        if (scenario.BackendRequirement == ScenarioBackendRequirement.AzureArchiveCapable &&
            !backend.Capabilities.SupportsArchiveTier)
        {
            return new RepresentativeScenarioRunResult(true, "Backend lacks archive-tier capability.");
        }

        await using var context = await backend.CreateContextAsync(cancellationToken);
        var definition = SyntheticRepositoryDefinitionFactory.Create(profile);
        string? previousSnapshotVersion = null;

        if (scenario.CacheState == ScenarioCacheState.Cold)
            await dependencies.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

        if (scenario.Name == "archive-tier-planning")
        {
            var archiveTierOutcome = await ExecuteArchiveTierScenarioAsync(
                context,
                definition,
                scenario,
                seed,
                cancellationToken);

            return new RepresentativeScenarioRunResult(false, ArchiveTierOutcome: archiveTierOutcome);
        }

        if (RequiresSetupArchive(scenario))
        {
            await using var setupFixture = await dependencies.CreateFixtureAsync(context, cancellationToken);
            await setupFixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V1, seed);

            var initialArchive = await setupFixture.ArchiveAsync(
                CreateArchiveOptions(setupFixture, useNoPointers: false, useRemoveLocal: false),
                cancellationToken);
            initialArchive.Success.ShouldBeTrue(initialArchive.ErrorMessage);
            previousSnapshotVersion = FormatSnapshotVersion(initialArchive.SnapshotTime);

            if (RequiresV2SetupArchive(scenario))
            {
                await setupFixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed);

                var v2Archive = await setupFixture.ArchiveAsync(
                    CreateArchiveOptions(setupFixture, useNoPointers: false, useRemoveLocal: false),
                    cancellationToken);
                v2Archive.Success.ShouldBeTrue(v2Archive.ErrorMessage);
            }

            if (scenario.CacheState == ScenarioCacheState.Warm)
                await setupFixture.PreserveLocalCacheAsync();
        }

        if (scenario.CacheState == ScenarioCacheState.Cold)
            await dependencies.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

        switch (scenario.Operation)
        {
            case ScenarioOperation.Archive:
                await using (var fixture = await dependencies.CreateFixtureAsync(context, cancellationToken))
                {
                    await fixture.MaterializeSourceAsync(definition, scenario.SourceVersion, seed);

                    var archiveResult = await fixture.ArchiveAsync(
                        CreateArchiveOptions(fixture, scenario.UseNoPointers, scenario.UseRemoveLocal),
                        cancellationToken);
                    archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
                }

                break;

            case ScenarioOperation.Restore:
                await ExecuteRestoreOperationsAsync(
                    context,
                    definition,
                    scenario,
                    seed,
                    previousSnapshotVersion,
                    dependencies,
                    cancellationToken);
                break;

            case ScenarioOperation.ArchiveThenRestore:
                await using (var fixture = await dependencies.CreateFixtureAsync(context, cancellationToken))
                {
                    await fixture.MaterializeSourceAsync(definition, scenario.SourceVersion, seed);

                    var archive = await fixture.ArchiveAsync(
                        CreateArchiveOptions(fixture, scenario.UseNoPointers, scenario.UseRemoveLocal),
                        cancellationToken);
                    archive.Success.ShouldBeTrue(archive.ErrorMessage);
                }

                await ExecuteRestoreOperationsAsync(
                    context,
                    definition,
                    scenario,
                    seed,
                    previousSnapshotVersion,
                    dependencies,
                    cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario.Operation));
        }

        return new RepresentativeScenarioRunResult(false);
    }

    private static async Task ExecuteRestoreOperationsAsync(
        E2EStorageBackendContext context,
        SyntheticRepositoryDefinition definition,
        RepresentativeScenarioDefinition scenario,
        int seed,
        string? previousSnapshotVersion,
        RepresentativeScenarioRunnerDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var restorePlans = CreateRestorePlans(scenario, previousSnapshotVersion);

        if (scenario.CacheState == ScenarioCacheState.Warm && scenario.RestoreTarget == ScenarioRestoreTarget.MultipleVersions)
        {
            var restoreFixtures = new List<IRepresentativeScenarioFixture>();

            try
            {
                foreach (var restorePlan in restorePlans)
                {
                    var restoreFixture = await dependencies.CreateFixtureAsync(context, cancellationToken);
                    restoreFixtures.Add(restoreFixture);

                    await PrepareRestoreConflictAsync(restoreFixture, definition, scenario, restorePlan.ExpectedVersion, seed);

                    var restoreResult = await restoreFixture.RestoreAsync(
                        restorePlan.Options with { RootDirectory = restoreFixture.RestoreRoot },
                        cancellationToken);
                    restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

                    if (dependencies.AssertRestoreTrees)
                    {
                        await AssertRestoreOutcomeAsync(restoreFixture, definition, scenario, restorePlan.ExpectedVersion, seed, restoreResult);
                    }
                }
            }
            finally
            {
                for (var i = restoreFixtures.Count - 1; i >= 0; i--)
                    await restoreFixtures[i].DisposeAsync();
            }

            return;
        }

        foreach (var restorePlan in restorePlans)
        {
            await using var restoreFixture = await dependencies.CreateFixtureAsync(context, cancellationToken);

            await PrepareRestoreConflictAsync(restoreFixture, definition, scenario, restorePlan.ExpectedVersion, seed);

            var restoreResult = await restoreFixture.RestoreAsync(
                restorePlan.Options with { RootDirectory = restoreFixture.RestoreRoot },
                cancellationToken);
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

            if (dependencies.AssertRestoreTrees)
            {
                await AssertRestoreOutcomeAsync(restoreFixture, definition, scenario, restorePlan.ExpectedVersion, seed, restoreResult);
            }
        }
    }

    private static bool RequiresV2SetupArchive(RepresentativeScenarioDefinition scenario)
    {
        return scenario.Operation switch
        {
            ScenarioOperation.Archive => scenario.ArchiveMode == ScenarioArchiveMode.NoChanges,
            ScenarioOperation.Restore => scenario.RestoreTarget switch
            {
                ScenarioRestoreTarget.Previous or ScenarioRestoreTarget.MultipleVersions => true,
                ScenarioRestoreTarget.Latest => scenario.SourceVersion == SyntheticRepositoryVersion.V2,
                _ => false,
            },
            ScenarioOperation.ArchiveThenRestore => false,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario.Operation)),
        };
    }

    private static bool RequiresSetupArchive(RepresentativeScenarioDefinition scenario)
    {
        return scenario.Operation switch
        {
            ScenarioOperation.Archive => scenario.ArchiveMode != ScenarioArchiveMode.Initial,
            ScenarioOperation.Restore => true,
            ScenarioOperation.ArchiveThenRestore => false,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario.Operation)),
        };
    }

    private static ArchiveCommandOptions CreateArchiveOptions(
        IRepresentativeScenarioFixture fixture,
        bool useNoPointers,
        bool useRemoveLocal)
    {
        return new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = BlobTier.Cool,
            NoPointers = useNoPointers,
            RemoveLocal = useRemoveLocal,
        };
    }

    private static ArchiveCommandOptions CreateArchiveTierOptions(IRepresentativeScenarioFixture fixture)
    {
        return new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = BlobTier.Archive,
        };
    }

    private static IReadOnlyList<RestoreExecutionPlan> CreateRestorePlans(
        RepresentativeScenarioDefinition scenario,
        string? previousSnapshotVersion)
    {
        var latest = new RestoreOptions
        {
            RootDirectory = string.Empty,
            Overwrite = scenario.UseOverwrite,
            Version = scenario.RestoreVersion == "previous"
                ? previousSnapshotVersion
                : scenario.RestoreVersion,
        };

        return scenario.RestoreTarget switch
        {
            ScenarioRestoreTarget.MultipleVersions =>
            [
                new RestoreExecutionPlan(
                    latest with { Version = previousSnapshotVersion },
                    SyntheticRepositoryVersion.V1),
                new RestoreExecutionPlan(
                    latest with { Version = null },
                    SyntheticRepositoryVersion.V2),
            ],
            _ =>
            [
                new RestoreExecutionPlan(
                    latest,
                    scenario.RestoreTarget == ScenarioRestoreTarget.Previous
                        ? SyntheticRepositoryVersion.V1
                        : scenario.SourceVersion),
            ],
        };
    }

    private static async Task PrepareRestoreConflictAsync(
        IRepresentativeScenarioFixture fixture,
        SyntheticRepositoryDefinition definition,
        RepresentativeScenarioDefinition scenario,
        SyntheticRepositoryVersion expectedVersion,
        int seed)
    {
        if (scenario.RestoreTarget != ScenarioRestoreTarget.Latest)
            return;

        if (scenario.Name is not "restore-local-conflict-no-overwrite" and not "restore-local-conflict-overwrite")
            return;

        var conflictPath = GetConflictPath(definition, expectedVersion);
        var fullPath = Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var conflictBytes = CreateConflictBytes(seed, conflictPath);
        await File.WriteAllBytesAsync(fullPath, conflictBytes);
    }

    private static async Task AssertRestoreOutcomeAsync(
        IRepresentativeScenarioFixture fixture,
        SyntheticRepositoryDefinition definition,
        RepresentativeScenarioDefinition scenario,
        SyntheticRepositoryVersion expectedVersion,
        int seed,
        RestoreResult restoreResult)
    {
        if (scenario.RestoreTarget == ScenarioRestoreTarget.None)
            return;

        if (!scenario.UseOverwrite && scenario.Name == "restore-local-conflict-no-overwrite")
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

            if (!scenario.UseNoPointers)
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

    private static string FormatSnapshotVersion(DateTimeOffset snapshotTime) =>
        snapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);

    private static string GetConflictPath(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion expectedVersion)
    {
        const string v1ChangedPath = "src/module-00/group-00/file-0000.bin";

        if (definition.Files.Any(file => file.Path == v1ChangedPath) &&
            expectedVersion == SyntheticRepositoryVersion.V1)
        {
            return v1ChangedPath;
        }

        return definition.Files[0].Path;
    }

    private static byte[] CreateConflictBytes(int seed, string path)
    {
        var bytes = new byte[1024];
        new Random(HashCode.Combine(seed, path, "restore-conflict")).NextBytes(bytes);
        return bytes;
    }

    private static async Task<ArchiveTierScenarioOutcome> ExecuteArchiveTierScenarioAsync(
        E2EStorageBackendContext context,
        SyntheticRepositoryDefinition definition,
        RepresentativeScenarioDefinition scenario,
        int seed,
        CancellationToken cancellationToken)
    {
        var azureBlobContainer = context.AzureBlobContainerService;
        azureBlobContainer.ShouldNotBeNull();
        context.Capabilities.SupportsArchiveTier.ShouldBeTrue();

        await using var fixture = await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Archive,
            ct: cancellationToken);
        await fixture.MaterializeSourceAsync(definition, scenario.SourceVersion, seed);

        var archiveResult = await fixture.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalRoot,
                UploadTier = BlobTier.Archive,
            }),
            cancellationToken).AsTask();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var tarChunkHash = await PollForArchiveTierTarChunkAsync(azureBlobContainer, cancellationToken);
        tarChunkHash.ShouldNotBeNullOrWhiteSpace();

        var contentHashToBytes = await ReadArchiveTierContentBytesAsync(fixture.LocalRoot, "src");

        var trackingSvc1 = new CopyTrackingBlobService(azureBlobContainer);
        var firstEstimateCaptured = false;
            var initialResult = await CreateArchiveTierRestoreHandler(
                fixture,
                context,
                trackingSvc1)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreRoot,
                TargetPath = "src",
                Overwrite = true,
                ConfirmRehydration = (estimate, _) =>
                {
                    firstEstimateCaptured = true;
                    (estimate.ChunksNeedingRehydration + estimate.ChunksPendingRehydration).ShouldBeGreaterThan(0);
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }), cancellationToken).AsTask();

        initialResult.Success.ShouldBeTrue(initialResult.ErrorMessage);

        var trackingSvc2 = new CopyTrackingBlobService(azureBlobContainer);
        var rerunResult = await CreateArchiveTierRestoreHandler(
                fixture,
                context,
                trackingSvc2)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = fixture.RestoreRoot,
                TargetPath = "src",
                Overwrite = true,
                ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            }), cancellationToken).AsTask();

        rerunResult.Success.ShouldBeTrue(rerunResult.ErrorMessage);

        await SideloadRehydratedTarChunkAsync(
            azureBlobContainer,
            tarChunkHash!,
            contentHashToBytes,
            cancellationToken);

        var cleanupDeletedChunks = 0;
        var readyRestoreRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(readyRestoreRoot);

        try
        {
            var readyResult = await fixture.CreateRestoreHandler().Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = readyRestoreRoot,
                TargetPath = "src",
                Overwrite = true,
                ConfirmCleanup = (count, _, _) =>
                {
                    cleanupDeletedChunks = count;
                    return Task.FromResult(true);
                },
            }), cancellationToken).AsTask();

            readyResult.Success.ShouldBeTrue(readyResult.ErrorMessage);

            var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-expected-{Guid.NewGuid():N}");
            try
            {
                var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(
                    definition,
                    scenario.SourceVersion,
                    seed,
                    expectedRoot);

                var expectedRestoreTree = FilterSnapshotToPrefix(expected, "src", trimPrefix: false);

                await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(
                    expectedRestoreTree,
                    readyRestoreRoot,
                    includePointerFiles: false);

                foreach (var relativePath in expectedRestoreTree.Files.Keys)
                {
                    var pointerPath = Path.Combine(
                        readyRestoreRoot,
                        (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                    File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {relativePath}");
                }
            }
            finally
            {
                if (Directory.Exists(expectedRoot))
                    Directory.Delete(expectedRoot, recursive: true);
            }

            return new ArchiveTierScenarioOutcome(
                firstEstimateCaptured,
                initialResult.ChunksPendingRehydration,
                initialResult.FilesRestored,
                rerunResult.ChunksPendingRehydration,
                trackingSvc2.CopyCalls.Count,
                readyResult.FilesRestored,
                readyResult.ChunksPendingRehydration,
                cleanupDeletedChunks);
        }
        finally
        {
            if (Directory.Exists(readyRestoreRoot))
                Directory.Delete(readyRestoreRoot, recursive: true);
        }
    }

    private static RestoreCommandHandler CreateArchiveTierRestoreHandler(
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

    private static async Task<string?> PollForArchiveTierTarChunkAsync(
        AzureBlobContainerService blobContainer,
        CancellationToken cancellationToken)
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

    private static async Task<Dictionary<string, byte[]>> ReadArchiveTierContentBytesAsync(
        string localRoot,
        string targetPath)
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

    private static async Task SideloadRehydratedTarChunkAsync(
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
        await blobContainer.UploadAsync(
            rehydratedBlobName,
            memoryStream,
            sourceMeta.Metadata,
            BlobTier.Hot,
            overwrite: true,
            cancellationToken: cancellationToken);
    }

    private static RepositoryTreeSnapshot FilterSnapshotToPrefix(
        RepositoryTreeSnapshot snapshot,
        string prefix,
        bool trimPrefix)
    {
        var normalizedPrefix = prefix.TrimEnd('/') + "/";

        return new RepositoryTreeSnapshot(snapshot.Files
            .Where(pair => pair.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => trimPrefix ? pair.Key[normalizedPrefix.Length..] : pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }

    private sealed class E2EScenarioFixtureAdapter(E2EFixture inner) : IRepresentativeScenarioFixture
    {
        public string LocalRoot => inner.LocalRoot;

        public string RestoreRoot => inner.RestoreRoot;

        public Task PreserveLocalCacheAsync() => inner.PreserveLocalCacheAsync();

        public Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
            SyntheticRepositoryDefinition definition,
            SyntheticRepositoryVersion version,
            int seed) => inner.MaterializeSourceAsync(definition, version, seed);

        public Task<ArchiveResult> ArchiveAsync(ArchiveCommandOptions options, CancellationToken ct = default) =>
            inner.CreateArchiveHandler().Handle(new ArchiveCommand(options), ct).AsTask();

        public Task<RestoreResult> RestoreAsync(RestoreOptions options, CancellationToken ct = default) =>
            inner.CreateRestoreHandler().Handle(new RestoreCommand(options), ct).AsTask();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
