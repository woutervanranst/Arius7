using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowRunnerDependencies
{
    public Func<E2EStorageBackendContext, string, CancellationToken, Task<E2EFixture>> CreateFixtureAsync { get; init; } =
        static (context, workflowRoot, cancellationToken) => RepresentativeWorkflowRunner.CreateFixtureAsync(context, workflowRoot, cancellationToken);
}

internal static class RepresentativeWorkflowRunner
{
    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, CancellationToken cancellationToken)
    {
        return await E2EFixture.CreateAsync(context.BlobContainer, context.AccountName, context.ContainerName, BlobTier.Cool, cancellationToken: cancellationToken);
    }

    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, string workflowRoot, CancellationToken cancellationToken)
    {
        return await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            tempRoot: workflowRoot,
            deleteTempRoot: static _ => { },
            cancellationToken: cancellationToken);
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
        var workflowRoot = Path.Combine(Path.GetTempPath(), "arius", $"arius-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflowRoot);
        var fixture = await dependencies.CreateFixtureAsync(context, workflowRoot, cancellationToken);
        RepresentativeWorkflowState? state = null;

        try
        {
            var versionedSourceRoot = Path.Combine(workflowRoot, "representative-source");
            Directory.CreateDirectory(versionedSourceRoot);

            state = new RepresentativeWorkflowState
            {
                Context            = context,
                CreateFixtureAsync = (backendContext, ct) => dependencies.CreateFixtureAsync(backendContext, workflowRoot, ct),
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
            }
            else
                await fixture.DisposeAsync();

            if (Directory.Exists(workflowRoot))
                Directory.Delete(workflowRoot, recursive: true);
        }
    }
}
