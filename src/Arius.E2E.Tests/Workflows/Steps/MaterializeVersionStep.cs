using Arius.E2E.Tests.Datasets;
using Arius.Tests.Shared.IO;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var versionedSourceFileSystem = new RelativeFileSystem(state.VersionedSourceDirectory);
        var versionState = state.VersionedSourceStates.TryGetValue(Version, out var existingState)
            && versionedSourceFileSystem.DirectoryExists(existingState.RootDirectory)
            ? existingState
            : await MaterializeVersionAsync(state, cancellationToken);

        FileSystemHelper.CopyDirectory(versionState.RootDirectory, state.Fixture.LocalDirectory);

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
                var versionRootPath = LocalDirectory.Parse(state.VersionedSourceDirectory.Resolve(RelativePath.Parse(nameof(SyntheticRepositoryVersion.V1))));
                return await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath, state.Fixture.Encryption);
            }
            case SyntheticRepositoryVersion.V2:
            {
                if (!state.VersionedSourceStates.TryGetValue(SyntheticRepositoryVersion.V1, out var v1State))
                    throw new InvalidOperationException("V1 source state must exist before materializing V2.");

                var versionedSourceFileSystem = new RelativeFileSystem(state.VersionedSourceDirectory);
                if (!versionedSourceFileSystem.DirectoryExists(v1State.RootDirectory))
                    v1State = await RematerializeV1Async(state, cancellationToken);

                var versionRootPath = LocalDirectory.Parse(state.VersionedSourceDirectory.Resolve(RelativePath.Parse(nameof(SyntheticRepositoryVersion.V2))));
                return await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(state.Definition, state.Seed, v1State.RootDirectory, versionRootPath, state.Fixture.Encryption);
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    internal static async Task<SyntheticRepositoryState> RematerializeV1Async(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var versionRootPath = LocalDirectory.Parse(state.VersionedSourceDirectory.Resolve(RelativePath.Parse(nameof(SyntheticRepositoryVersion.V1))));
        var versionState = await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath, state.Fixture.Encryption);
        state.VersionedSourceStates[SyntheticRepositoryVersion.V1] = versionState;
        return versionState;
    }
}
