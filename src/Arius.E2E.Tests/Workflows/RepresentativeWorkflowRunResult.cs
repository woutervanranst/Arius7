namespace Arius.E2E.Tests.Workflows;

internal sealed record RepresentativeWorkflowRunResult(
    bool WasSkipped,
    string? SkipReason = null,
    ArchiveTierWorkflowOutcome? ArchiveTierOutcome = null);

internal sealed record ArchiveTierWorkflowOutcome(
    bool WasCostEstimateCaptured,
    int InitialPendingChunks,
    int InitialFilesRestored,
    int PendingChunksOnRerun,
    int RerunCopyCalls,
    int ReadyFilesRestored,
    int ReadyPendingChunks,
    int CleanupDeletedChunks,
    int PendingRehydratedBlobCount);
