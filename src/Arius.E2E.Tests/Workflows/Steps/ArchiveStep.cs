using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ArchiveStep(
    string Name,
    BlobTier UploadTier = BlobTier.Cool,
    bool NoPointers = false,
    bool RemoveLocal = false,
    bool CaptureNoOpPreCounts = false) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        if (CaptureNoOpPreCounts)
        {
            state.ChunkBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
                state.Context.BlobContainer,
                BlobPaths.Chunks,
                cancellationToken);
            state.FileTreeBlobCountBeforeNoOpArchive = await WorkflowBlobAssertions.CountBlobsAsync(
                state.Context.BlobContainer,
                BlobPaths.FileTrees,
                cancellationToken);
        }

        var options = new ArchiveCommandOptions
        {
            RootDirectory = state.Fixture.LocalRoot,
            UploadTier = UploadTier,
            NoPointers = NoPointers,
            RemoveLocal = RemoveLocal,
        };

        var result = await state.Fixture.CreateArchiveHandler()
            .Handle(new ArchiveCommand(options), cancellationToken)
            .AsTask();

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");
        state.PreviousSnapshotVersion = state.LatestSnapshotVersion;
        state.LatestSnapshotVersion = result.SnapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);
    }
}
