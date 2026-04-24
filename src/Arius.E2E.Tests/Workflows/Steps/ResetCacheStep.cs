using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ResetCacheStep(string Name = "reset-cache") : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var preservedSourceRoot = Path.Combine(Path.GetTempPath(), $"arius-reset-cache-source-{Guid.NewGuid():N}");
        var hadSourceTree = Directory.Exists(state.Fixture.LocalRoot);

        if (hadSourceTree)
            Directory.Move(state.Fixture.LocalRoot, preservedSourceRoot);

        await state.Fixture.DisposeAsync();
        await E2EFixture.ResetLocalCacheAsync(state.Context.AccountName, state.Context.ContainerName);
        state.Fixture = await state.CreateFixtureAsync(state.Context, cancellationToken);

        try
        {
            if (hadSourceTree)
            {
                if (Directory.Exists(state.Fixture.LocalRoot))
                    Directory.Delete(state.Fixture.LocalRoot, recursive: true);

                Directory.Move(preservedSourceRoot, state.Fixture.LocalRoot);
            }
        }
        finally
        {
            if (Directory.Exists(preservedSourceRoot))
                Directory.Delete(preservedSourceRoot, recursive: true);
        }
    }
}
