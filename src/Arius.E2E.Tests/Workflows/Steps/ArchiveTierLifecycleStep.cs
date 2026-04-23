using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Services;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed record ArchiveTierLifecycleStep(string Name, string TargetPath = "src") : IRepresentativeWorkflowStep
{
    public async Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
    {
        var azureBlobContainer = state.Context.AzureBlobContainerService;
        azureBlobContainer.ShouldNotBeNull($"{Name}: archive-tier workflow requires Azure blob storage.");
        state.Context.Capabilities.SupportsArchiveTier.ShouldBeTrue($"{Name}: backend must support archive tier.");

        var sourceVersion = state.CurrentSourceVersion
            ?? throw new InvalidOperationException($"{Name}: current source version is not available.");

        await state.Fixture.DisposeAsync();
        state.Fixture = await state.CreateFixtureAsync(state.Context, cancellationToken);

        await state.Fixture.MaterializeSourceAsync(state.Definition, sourceVersion, state.Seed);

        var archiveResult = await RepresentativeWorkflowRunner.ArchiveAsync(
            state.Fixture,
            RepresentativeWorkflowRunner.CreateArchiveTierOptions(state.Fixture),
            cancellationToken);

        archiveResult.Success.ShouldBeTrue($"{Name}: archive failed: {archiveResult.ErrorMessage}");

        var tarChunkHash = await RepresentativeWorkflowRunner.PollForArchiveTierTarChunkAsync(azureBlobContainer, cancellationToken);
        tarChunkHash.ShouldNotBeNullOrWhiteSpace($"{Name}: expected at least one archive-tier tar chunk.");

        var contentHashToBytes = await RepresentativeWorkflowRunner.ReadArchiveTierContentBytesAsync(
            state.Fixture.LocalRoot,
            TargetPath);

        var firstEstimateCaptured = false;
        var firstTrackingBlobService = new CopyTrackingBlobService(azureBlobContainer);
        var initialResult = await RepresentativeWorkflowRunner.CreateArchiveTierRestoreHandler(state.Fixture, state.Context, firstTrackingBlobService)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmRehydration = (estimate, _) =>
                {
                    firstEstimateCaptured = true;
                    (estimate.ChunksNeedingRehydration + estimate.ChunksPendingRehydration)
                        .ShouldBeGreaterThan(0, $"{Name}: pending archive-tier restore should request rehydration.");
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            }), cancellationToken).AsTask();

        initialResult.Success.ShouldBeTrue($"{Name}: pending restore failed: {initialResult.ErrorMessage}");

        var pendingRehydratedBlobCount = await RepresentativeWorkflowRunner.CountBlobsAsync(
            azureBlobContainer,
            BlobPaths.ChunksRehydrated,
            cancellationToken);

        var rerunTrackingBlobService = new CopyTrackingBlobService(azureBlobContainer);
        var rerunResult = await RepresentativeWorkflowRunner.CreateArchiveTierRestoreHandler(state.Fixture, state.Context, rerunTrackingBlobService)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = state.Fixture.RestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmRehydration = (_, _) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            }), cancellationToken).AsTask();

        rerunResult.Success.ShouldBeTrue($"{Name}: pending rerun failed: {rerunResult.ErrorMessage}");

        await RepresentativeWorkflowRunner.DeleteBlobsAsync(
            azureBlobContainer,
            BlobPaths.ChunksRehydrated,
            cancellationToken);

        await RepresentativeWorkflowRunner.SideloadRehydratedTarChunkAsync(
            azureBlobContainer,
            tarChunkHash!,
            contentHashToBytes,
            cancellationToken);

        var cleanupDeletedChunks = 0;
        var readyRestoreRoot = Path.Combine(Path.GetTempPath(), $"arius-archive-tier-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(readyRestoreRoot);

        try
        {
            var readyResult = await state.Fixture.CreateRestoreHandler().Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = readyRestoreRoot,
                TargetPath = TargetPath,
                Overwrite = true,
                ConfirmCleanup = (count, _, _) =>
                {
                    cleanupDeletedChunks = count;
                    return Task.FromResult(true);
                },
            }), cancellationToken).AsTask();

            readyResult.Success.ShouldBeTrue($"{Name}: ready restore failed: {readyResult.ErrorMessage}");

            await RepresentativeWorkflowRunner.AssertArchiveTierRestoreOutcomeAsync(
                state.Definition,
                sourceVersion,
                state.Seed,
                TargetPath,
                readyRestoreRoot);

            state.ArchiveTierOutcome = new ArchiveTierWorkflowOutcome(
                firstEstimateCaptured,
                initialResult.ChunksPendingRehydration,
                initialResult.FilesRestored,
                rerunResult.ChunksPendingRehydration,
                rerunTrackingBlobService.CopyCalls.Count,
                readyResult.FilesRestored,
                readyResult.ChunksPendingRehydration,
                cleanupDeletedChunks,
                pendingRehydratedBlobCount);
        }
        finally
        {
            if (Directory.Exists(readyRestoreRoot))
                Directory.Delete(readyRestoreRoot, recursive: true);
        }
    }
}
