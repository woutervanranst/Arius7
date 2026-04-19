using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
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
}

internal sealed record RepresentativeScenarioRunResult(
    bool WasSkipped,
    string? SkipReason = null);

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

        if (scenario.CacheState == ScenarioCacheState.Cold)
            await dependencies.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

        await using (var setupFixture = await dependencies.CreateFixtureAsync(context, cancellationToken))
        {
            await setupFixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V1, seed);

            var initialArchive = await setupFixture.ArchiveAsync(
                CreateArchiveOptions(setupFixture, useNoPointers: false, useRemoveLocal: false),
                cancellationToken);
            initialArchive.Success.ShouldBeTrue(initialArchive.ErrorMessage);

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
                await ExecuteRestoreOperationsAsync(context, scenario, dependencies, cancellationToken);
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

                await ExecuteRestoreOperationsAsync(context, scenario, dependencies, cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario.Operation));
        }

        return new RepresentativeScenarioRunResult(false);
    }

    private static async Task ExecuteRestoreOperationsAsync(
        E2EStorageBackendContext context,
        RepresentativeScenarioDefinition scenario,
        RepresentativeScenarioRunnerDependencies dependencies,
        CancellationToken cancellationToken)
    {
        foreach (var restoreOptions in CreateRestoreOptions(scenario))
        {
            await using var restoreFixture = await dependencies.CreateFixtureAsync(context, cancellationToken);
            var restoreResult = await restoreFixture.RestoreAsync(
                restoreOptions with { RootDirectory = restoreFixture.RestoreRoot },
                cancellationToken);
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        }
    }

    private static bool RequiresV2SetupArchive(RepresentativeScenarioDefinition scenario)
    {
        return scenario.Operation switch
        {
            ScenarioOperation.Archive => scenario.ArchiveMode == ScenarioArchiveMode.NoChanges,
            ScenarioOperation.Restore => scenario.RestoreTarget is ScenarioRestoreTarget.Previous or ScenarioRestoreTarget.Latest or ScenarioRestoreTarget.MultipleVersions,
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

    private static IReadOnlyList<RestoreOptions> CreateRestoreOptions(RepresentativeScenarioDefinition scenario)
    {
        var latest = new RestoreOptions
        {
            RootDirectory = string.Empty,
            Overwrite = scenario.UseOverwrite,
            Version = scenario.RestoreVersion,
        };

        return scenario.RestoreTarget switch
        {
            ScenarioRestoreTarget.MultipleVersions =>
            [
                latest with { Version = "previous" },
                latest with { Version = null },
            ],
            _ => [latest],
        };
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
