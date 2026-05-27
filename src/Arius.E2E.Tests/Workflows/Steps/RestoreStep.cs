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
        state.Fixture.RestoreFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        state.Fixture.RestoreFileSystem.CreateDirectory(RelativePath.Root);

        var version = Helpers.ResolveVersion(state, Target);

        var result = await Helpers.RestoreAsync(state.Fixture, Overwrite, version, cancellationToken);

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");

        await Helpers.AssertRestoreOutcomeAsync(
            state.Fixture,
            state,
            ExpectedVersion,
            useNoPointers: !ExpectPointers,
            result,
            preserveConflictBytes: false);
    }
}
