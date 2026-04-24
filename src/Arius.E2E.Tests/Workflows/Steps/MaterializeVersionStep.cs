using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record MaterializeVersionStep(SyntheticRepositoryVersion Version) : IRepresentativeWorkflowStep
{
    public string Name => $"materialize-{Version}";

    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        SyntheticRepositoryState versionState = Version switch
        {
            SyntheticRepositoryVersion.V1 => await MaterializeV1Async(state),
            SyntheticRepositoryVersion.V2 => await MaterializeV2Async(state),
            _ => throw new ArgumentOutOfRangeException()
        };

        state.CurrentSyntheticRepositoryState = versionState;
        state.VersionedSourceStates[Version] = versionState;
        state.CurrentSourceVersion = Version;

        static async Task<SyntheticRepositoryState> MaterializeV1Async(RepresentativeWorkflowState state)
        {
            var versionRootPath = Path.Combine(state.VersionedSourceRoot, SyntheticRepositoryVersion.V1.ToString());
            var versionState = await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath);
            await CopyDirectoryAsync(versionState.RootPath, state.Fixture.LocalRoot);
            return versionState;
        }

        static async Task<SyntheticRepositoryState> MaterializeV2Async(RepresentativeWorkflowState state)
        {
            if (!state.VersionedSourceStates.TryGetValue(SyntheticRepositoryVersion.V1, out var v1State))
                throw new InvalidOperationException("V1 source state must exist before materializing V2.");

            var versionRootPath = Path.Combine(state.VersionedSourceRoot, SyntheticRepositoryVersion.V2.ToString());
            var versionState = await SyntheticRepositoryMaterializer.MaterializeV2FromExistingAsync(
                state.Definition,
                state.Seed,
                v1State.RootPath,
                versionRootPath);
            await CopyDirectoryAsync(versionState.RootPath, state.Fixture.LocalRoot);
            return versionState;
        }

        static async Task CopyDirectoryAsync(string sourceRootPath, string targetRootPath)
        {
            if (Directory.Exists(targetRootPath))
                Directory.Delete(targetRootPath, recursive: true);

            Directory.CreateDirectory(targetRootPath);

            foreach (var directoryPath in Directory.EnumerateDirectories(sourceRootPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRootPath, directoryPath);
                Directory.CreateDirectory(Path.Combine(targetRootPath, relativePath));
            }

            foreach (var filePath in Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRootPath, filePath);
                var targetPath = Path.Combine(targetRootPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                File.Copy(filePath, targetPath, overwrite: true);
            }
        }
    }
}
