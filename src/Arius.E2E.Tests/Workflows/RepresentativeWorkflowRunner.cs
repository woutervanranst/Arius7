using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.Tests.Shared.Helpers;

namespace Arius.E2E.Tests.Workflows;

internal sealed class RepresentativeWorkflowRunnerDependencies
{
    public Func<E2EStorageBackendContext, LocalRootPath, CancellationToken, Task<E2EFixture>> CreateFixtureAsync { get; init; } =
        static (context, workflowRoot, cancellationToken) => RepresentativeWorkflowRunner.CreateFixtureAsync(context, workflowRoot, cancellationToken);
}

internal static class RepresentativeWorkflowRunner
{
    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, CancellationToken cancellationToken)
    {
        return await E2EFixture.CreateAsync(context.BlobContainer, context.AccountName, context.ContainerName, BlobTier.Cool, cancellationToken: cancellationToken);
    }

    internal static async Task<E2EFixture> CreateFixtureAsync(E2EStorageBackendContext context, LocalRootPath workflowRoot, CancellationToken cancellationToken)
    {
        var fixtureRoot = workflowRoot.GetSubdirectoryRoot("fixture");

        return await E2EFixture.CreateAsync(
            context.BlobContainer,
            context.AccountName,
            context.ContainerName,
            BlobTier.Cool,
            tempRoot: fixtureRoot,
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
        var workflowRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), "arius", $"arius-test-{Guid.NewGuid():N}"));
        E2EFixture? fixture = null;
        RepresentativeWorkflowState? state = null;

        workflowRoot.CreateDirectory();

        try
        {
            fixture = await dependencies.CreateFixtureAsync(context, workflowRoot, cancellationToken);

            var versionedSourceRoot = workflowRoot.GetSubdirectoryRoot("representative-source");
            versionedSourceRoot.CreateDirectory();

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

            if (workflowRoot.ExistsDirectory)
                workflowRoot.DeleteDirectory(recursive: true);
        }
    }
}
