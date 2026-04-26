using Arius.E2E.Tests.Datasets;
using Arius.Tests.Shared.IO;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        SyntheticRepositoryState versionState;

        switch (Version)
        {
            case SyntheticRepositoryVersion.V1:
            {
                var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V1));
                versionState = await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath, state.Fixture.Encryption);
                break;
            }
            case SyntheticRepositoryVersion.V2:
            {
                if (!state.VersionedSourceStates.TryGetValue(SyntheticRepositoryVersion.V1, out var v1State))
                    throw new InvalidOperationException("V1 source state must exist before materializing V2.");

                if (!Directory.Exists(v1State.RootPath))
                    v1State = await RematerializeV1Async(state, cancellationToken);

                var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V2));
                versionState = await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(state.Definition, state.Seed, v1State.RootPath, versionRootPath, state.Fixture.Encryption);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        FileSystemHelper.CopyDirectory(versionState.RootPath, state.Fixture.LocalRoot);

        state.CurrentSyntheticRepositoryState = versionState;
        state.VersionedSourceStates[Version]  = versionState;
        state.CurrentSourceVersion            = Version;
    }

    internal static async Task<SyntheticRepositoryState> RematerializeV1Async(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var versionRootPath = Path.Combine(state.VersionedSourceRoot, nameof(SyntheticRepositoryVersion.V1));
        var versionState = await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath, state.Fixture.Encryption);
        state.VersionedSourceStates[SyntheticRepositoryVersion.V1] = versionState;
        return versionState;
    }
}
