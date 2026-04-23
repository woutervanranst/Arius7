using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ResetCacheStep(string Name = "reset-cache") : IRepresentativeWorkflowStep
{
    public Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        => E2EFixture.ResetLocalCacheAsync(state.Context.AccountName, state.Context.ContainerName);
}
