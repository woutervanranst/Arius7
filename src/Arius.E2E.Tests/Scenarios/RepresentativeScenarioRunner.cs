using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Scenarios;

internal sealed record RepresentativeScenarioRunResult(
    bool WasSkipped,
    string? SkipReason = null);

internal static class RepresentativeScenarioRunner
{
    public static async Task<RepresentativeScenarioRunResult> RunAsync(
        IE2EStorageBackend backend,
        RepresentativeScenarioDefinition scenario,
        SyntheticRepositoryProfile profile,
        int seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(scenario);

        if (scenario.BackendRequirement == ScenarioBackendRequirement.AzureArchiveCapable &&
            !backend.Capabilities.SupportsArchiveTier)
        {
            return new RepresentativeScenarioRunResult(true, "Backend lacks archive-tier capability.");
        }

        await using var context = await backend.CreateContextAsync(cancellationToken);

        if (scenario.CacheState == ScenarioCacheState.Cold)
            await E2EFixture.ResetLocalCacheAsync(context.AccountName, context.ContainerName);

        await using var fixture = await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            ct: cancellationToken);

        var definition = SyntheticRepositoryDefinitionFactory.Create(profile);

        await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V1, seed);

        var initialArchive = await fixture.ArchiveAsync(cancellationToken);
        initialArchive.Success.ShouldBeTrue(initialArchive.ErrorMessage);

        if (scenario.SourceVersion == SyntheticRepositoryVersion.V2)
            await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed);

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
}
