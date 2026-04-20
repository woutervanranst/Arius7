using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

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
    string? SkipReason = null);

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

    private static IReadOnlyList<RestoreExecutionPlan> CreateRestorePlans(
        RepresentativeScenarioDefinition scenario,
        string? previousSnapshotVersion)
    {
        var latest = new RestoreOptions
        {
            RootDirectory = string.Empty,
            Overwrite = scenario.UseOverwrite,
            NoPointers = true,
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

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(expected, fixture.RestoreRoot);
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
