using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ResetCacheStep(string Name = "reset-cache") : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        await state.Fixture.DisposeAsync();
        await E2EFixture.ResetLocalCacheAsync(state.Context.AccountName, state.Context.ContainerName);
        state.Fixture = await state.CreateFixtureAsync(state.Context, cancellationToken);
    }
}
