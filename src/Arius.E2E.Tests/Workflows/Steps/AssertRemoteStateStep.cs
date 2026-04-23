namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record AssertRemoteStateStep(
    string Name,
    bool CaptureNoOpPreCounts = false,
    bool AssertNoOpStability = false) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var remoteState = await WorkflowBlobAssertions.AssertRemoteStateAsync(
            state,
            AssertNoOpStability,
            cancellationToken);

        if (!CaptureNoOpPreCounts)
            return;

        state.NoOpArchiveBaseline = new WorkflowNoOpArchiveBaseline(
            remoteState.LatestRootHash,
            remoteState.ChunkCount,
            remoteState.FileTreeCount);
    }
}
