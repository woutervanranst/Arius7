using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

/// <summary>
/// Seeds a conflicting local file in the restore target and verifies that restore either preserves or replaces that file depending on the requested overwrite mode.
/// </summary>
internal sealed record AssertConflictBehaviorStep(string Name, WorkflowRestoreTarget Target, SyntheticRepositoryVersion ExpectedVersion, bool Overwrite, bool ExpectPointers = true) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (Directory.Exists(state.Fixture.RestoreRoot))
            Directory.Delete(state.Fixture.RestoreRoot, recursive: true);

        Directory.CreateDirectory(state.Fixture.RestoreRoot);

        await Helpers.WriteRestoreConflictAsync(
            state.Fixture,
            state.Definition,
            ExpectedVersion,
            state.Seed);

        var version = Helpers.ResolveVersion(state, Target);

        var result = await Helpers.RestoreAsync(state.Fixture, Overwrite, version, cancellationToken);

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        await Helpers.AssertRestoreOutcomeAsync(
            state.Fixture,
            state,
            ExpectedVersion,
            useNoPointers: !ExpectPointers,
            result,
            preserveConflictBytes: !Overwrite);
    }
}
