using Arius.E2E.Tests.Datasets;
using Arius.Core.Shared.Paths;

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
        if (state.Fixture.RestoreRoot.ExistsDirectory)
            state.Fixture.RestoreRoot.DeleteDirectory(recursive: true);

        state.Fixture.RestoreRoot.CreateDirectory();

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
