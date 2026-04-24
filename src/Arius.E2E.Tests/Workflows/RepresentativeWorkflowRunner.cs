using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Core.Shared.Storage;

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
            var versionedSourceRoot = Path.Combine(Path.GetTempPath(), $"arius-representative-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(versionedSourceRoot);

            state = new RepresentativeWorkflowState
            {
                Context            = context,
                CreateFixtureAsync = dependencies.CreateFixtureAsync,
                Fixture            = fixture,
                Definition         = SyntheticRepositoryDefinitionFactory.Create(workflow.Profile),
                Seed               = workflow.Seed,
                VersionedSourceRoot = versionedSourceRoot,
            };

            foreach (var step in workflow.Steps)
                await step.ExecuteAsync(state, cancellationToken);

            return new RepresentativeWorkflowRunResult(false, ArchiveTierOutcome: state.ArchiveTierOutcome);
        }
        finally
        {
            if (state is not null)
            {
                await state.Fixture.DisposeAsync();
                if (Directory.Exists(state.VersionedSourceRoot))
                    Directory.Delete(state.VersionedSourceRoot, recursive: true);
            }
            else
                await fixture.DisposeAsync();
        }
    }
}
