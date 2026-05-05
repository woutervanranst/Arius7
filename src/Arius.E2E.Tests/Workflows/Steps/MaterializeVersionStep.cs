using Arius.E2E.Tests.Datasets;
using Arius.Tests.Shared.IO;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var versionState = state.VersionedSourceStates.TryGetValue(Version, out var existingState) && Directory.Exists(existingState.RootPath)
            ? existingState
            : await MaterializeVersionAsync(state, cancellationToken);

        FileSystemHelper.CopyDirectory(versionState.RootPath, state.Fixture.LocalRoot);

        state.CurrentSyntheticRepositoryState = versionState;
        state.VersionedSourceStates[Version]  = versionState;
        state.CurrentSourceVersion            = Version;
    }

    private async Task<SyntheticRepositoryState> MaterializeVersionAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        switch (Version)
        {
            case SyntheticRepositoryVersion.V1:
            {
                var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V1));
                return await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, RootOf(versionRootPath), state.Fixture.Encryption);
            }
            case SyntheticRepositoryVersion.V2:
            {
                if (!state.VersionedSourceStates.TryGetValue(SyntheticRepositoryVersion.V1, out var v1State))
                    throw new InvalidOperationException("V1 source state must exist before materializing V2.");

                if (!Directory.Exists(v1State.RootPath))
                    v1State = await RematerializeV1Async(state, cancellationToken);

                var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V2));
                return await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(state.Definition, state.Seed, RootOf(v1State.RootPath), RootOf(versionRootPath), state.Fixture.Encryption);
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    internal static async Task<SyntheticRepositoryState> RematerializeV1Async(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V1));
        var versionState = await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, RootOf(versionRootPath), state.Fixture.Encryption);
        state.VersionedSourceStates[SyntheticRepositoryVersion.V1] = versionState;
        return versionState;
    }
}
