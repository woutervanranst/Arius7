using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ArchiveStep(string Name, BlobTier UploadTier = BlobTier.Cool, bool NoPointers = false, bool RemoveLocal = false, bool CaptureNoOpPreCounts = false) : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var latestBeforeArchive = state.LatestSnapshotVersion;

        if (CaptureNoOpPreCounts)
        {
            state.ChunkBlobCountBeforeNoOpArchive    = await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.Chunks,    cancellationToken);
            state.FileTreeBlobCountBeforeNoOpArchive = await Helpers.CountBlobsAsync(state.Context.BlobContainer, BlobPaths.FileTrees, cancellationToken);
            state.SnapshotVersionBeforeNoOpArchive = latestBeforeArchive;
        }

        var options = new ArchiveCommandOptions
        {
            RootDirectory = state.Fixture.LocalRoot,
            UploadTier    = UploadTier,
            NoPointers    = NoPointers,
            RemoveLocal   = RemoveLocal,
        };

        var result = await state.Fixture.CreateArchiveHandler()
            .Handle(new ArchiveCommand(options), cancellationToken)
            .AsTask();

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");
        var resultVersion = result.SnapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);
        if (!string.Equals(resultVersion, state.LatestSnapshotVersion, StringComparison.Ordinal))
        {
            state.PreviousSnapshotVersion = state.LatestSnapshotVersion;
            state.LatestSnapshotVersion = resultVersion;
        }

        if (CaptureNoOpPreCounts)
            state.NoOpArchivePreservedSnapshot = string.Equals(resultVersion, latestBeforeArchive, StringComparison.Ordinal);
    }
}
