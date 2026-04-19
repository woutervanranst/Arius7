using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Scenarios;

internal interface IRepresentativeScenarioFixture : IAsyncDisposable
{
    Task PreserveLocalCacheAsync();

    Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion version,
        int seed);

    Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default);

    Task<RestoreResult> RestoreAsync(CancellationToken ct = default);
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

            var initialArchive = await setupFixture.ArchiveAsync(cancellationToken);
            initialArchive.Success.ShouldBeTrue(initialArchive.ErrorMessage);

            if (scenario.SourceVersion == SyntheticRepositoryVersion.V2)
            {
                await setupFixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed);

                var v2Archive = await setupFixture.ArchiveAsync(cancellationToken);
                v2Archive.Success.ShouldBeTrue(v2Archive.ErrorMessage);
            }

            if (scenario.CacheState == ScenarioCacheState.Warm)
                await setupFixture.PreserveLocalCacheAsync();
        }

        if (scenario.CacheState == ScenarioCacheState.Cold)
            await dependencies.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

        await using var fixture = await dependencies.CreateFixtureAsync(context, cancellationToken);

        switch (scenario.Operation)
        {
            case ScenarioOperation.Archive:
                var archiveResult = await fixture.ArchiveAsync(cancellationToken);
                archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
                break;

            case ScenarioOperation.Restore:
                var restoreResult = await fixture.RestoreAsync(cancellationToken);
                restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
                break;

            case ScenarioOperation.ArchiveThenRestore:
                var archive = await fixture.ArchiveAsync(cancellationToken);
                archive.Success.ShouldBeTrue(archive.ErrorMessage);

                var restore = await fixture.RestoreAsync(cancellationToken);
                restore.Success.ShouldBeTrue(restore.ErrorMessage);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario.Operation));
        }

        return new RepresentativeScenarioRunResult(false);
    }

    private sealed class E2EScenarioFixtureAdapter(E2EFixture inner) : IRepresentativeScenarioFixture
    {
        public Task PreserveLocalCacheAsync() => inner.PreserveLocalCacheAsync();

        public Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
            SyntheticRepositoryDefinition definition,
            SyntheticRepositoryVersion version,
            int seed) => inner.MaterializeSourceAsync(definition, version, seed);

        public Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default) => inner.ArchiveAsync(ct);

        public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) => inner.RestoreAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
