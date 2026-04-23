using Arius.Core.Features.RestoreCommand;
using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record AssertConflictBehaviorStep(
    string Name,
    WorkflowRestoreTarget Target,
    SyntheticRepositoryVersion ExpectedVersion,
    bool Overwrite,
    bool ExpectPointers = true) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (Directory.Exists(state.Fixture.RestoreRoot))
            Directory.Delete(state.Fixture.RestoreRoot, recursive: true);

        Directory.CreateDirectory(state.Fixture.RestoreRoot);

        await RepresentativeWorkflowRunner.WriteRestoreConflictAsync(
            state.Fixture,
            state.Definition,
            ExpectedVersion,
            state.Seed);

        var version = Target switch
        {
            WorkflowRestoreTarget.Previous => state.PreviousSnapshotVersion ?? throw new InvalidOperationException("Previous snapshot version is not available."),
            _ => null,
        };

        var result = await RepresentativeWorkflowRunner.RestoreAsync(
            state.Fixture,
            new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                Overwrite = Overwrite,
                Version = version,
            },
            cancellationToken);

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        await RepresentativeWorkflowRunner.AssertRestoreOutcomeAsync(
            state.Fixture,
            state.Definition,
            ExpectedVersion,
            state.Seed,
            useNoPointers: !ExpectPointers,
            result,
            preserveConflictBytes: !Overwrite);
    }
}
