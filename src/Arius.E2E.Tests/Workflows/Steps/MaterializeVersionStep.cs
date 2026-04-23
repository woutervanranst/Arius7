using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        state.CurrentMaterializedSnapshot = await state.Fixture.MaterializeSourceAsync(
            state.Definition,
            Version,
            state.Seed);
        state.CurrentSourceVersion = Version;
    }
}
