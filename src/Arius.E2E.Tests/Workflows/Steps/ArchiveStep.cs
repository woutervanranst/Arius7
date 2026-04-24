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

        var result = await RepresentativeWorkflowRunner.ArchiveAsync(
            state.Fixture,
            RepresentativeWorkflowRunner.CreateArchiveOptions(
                state.Fixture,
                uploadTier: UploadTier,
                useNoPointers: NoPointers,
                useRemoveLocal: RemoveLocal),
            cancellationToken);

        result.Success.ShouldBeTrue($"{Name}: {result.ErrorMessage}");
        state.PreviousSnapshotVersion = state.LatestSnapshotVersion;
        state.LatestSnapshotVersion = RepresentativeWorkflowRunner.FormatSnapshotVersion(result.SnapshotTime);
    }
}
