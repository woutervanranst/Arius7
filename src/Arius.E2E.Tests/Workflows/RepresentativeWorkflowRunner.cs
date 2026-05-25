using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Tests.Shared;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowRunnerDependencies
{
    public Func<E2EStorageBackendContext, LocalDirectory, CancellationToken, Task<E2EFixture>> CreateFixtureAsync { get; init; } =
        static (context, workflowRoot, cancellationToken) => RepresentativeWorkflowRunner.CreateFixtureAsync(context, workflowRoot, cancellationToken);
}

internal static class RepresentativeWorkflowRunner
{
    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, LocalDirectory workflowRoot, CancellationToken cancellationToken)
    {
        var fixtureRoot = LocalDirectory.Parse(workflowRoot.Resolve(RelativePath.Parse("fixture")));

        return await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            tempRoot: fixtureRoot,
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
        var workflowDirectory = TestTempRoots.CreateDirectory($"test-{Guid.NewGuid():N}");
        E2EFixture? fixture = null;
        RepresentativeWorkflowState? state = null;
        var workflowFileSystem = new RelativeFileSystem(workflowDirectory);

        workflowFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            fixture = await dependencies.CreateFixtureAsync(context, workflowDirectory, cancellationToken);

            var versionedSourceRoot = workflowDirectory / "representative-source";
            workflowFileSystem.CreateDirectory(RelativePath.Parse("representative-source"));

            state = new RepresentativeWorkflowState
            {
                Context                  = context,
                CreateFixtureAsync       = (backendContext, ct) => dependencies.CreateFixtureAsync(backendContext, workflowDirectory, ct),
                Fixture                  = fixture,
                Definition               = SyntheticRepositoryDefinitionFactory.Create(workflow.Profile),
                Seed                     = workflow.Seed,
                WorkflowDirectory        = workflowDirectory,
                VersionedSourceDirectory = versionedSourceRoot,
            };

            foreach (var step in workflow.Steps)
                await step.ExecuteAsync(state, cancellationToken);

            return new RepresentativeWorkflowRunResult(false, ArchiveTierOutcome: state.ArchiveTierOutcome);
        }
        finally
        {
            try
            {
                if (state is not null)
                {
                    await state.Fixture.DisposeAsync();
                }
                else if (fixture is not null)
                {
                    await fixture.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            workflowFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }
}
