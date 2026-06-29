namespace Arius.E2E.Tests.Workflows.Steps;

/// <summary>
/// Seeds a conflicting local file in the restore target and verifies that restore either preserves or replaces that file depending on the requested overwrite mode.
/// </summary>
internal sealed record AssertConflictBehaviorStep(string Name, WorkflowRestoreTarget Target, SyntheticRepositoryVersion ExpectedVersion, bool Overwrite, bool ExpectPointers = true) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        state.Fixture.RestoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        state.Fixture.RestoreFileSystem.CreateDirectory(RelativePath.Root);

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
            useWritePointers: ExpectPointers,
            result,
            preserveConflictBytes: !Overwrite);
    }
}
