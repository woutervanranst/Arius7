using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal enum WorkflowRestoreTarget
{
    Latest,
    Previous,
}

internal sealed record RestoreStep(string Name, WorkflowRestoreTarget Target, SyntheticRepositoryVersion ExpectedVersion, bool Overwrite = true, bool ExpectPointers = true) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (Directory.Exists(state.Fixture.RestoreRoot))
            Directory.Delete(state.Fixture.RestoreRoot, recursive: true);

        Directory.CreateDirectory(state.Fixture.RestoreRoot);

        var version = Helpers.ResolveVersion(state, Target);

        var result = await Helpers.RestoreAsync(state.Fixture, Overwrite, version, cancellationToken);

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        await Helpers.AssertRestoreOutcomeAsync(
            state.Fixture,
            state.Definition,
            ExpectedVersion,
            state.Seed,
            useNoPointers: !ExpectPointers,
            result,
            preserveConflictBytes: false);
    }
}
