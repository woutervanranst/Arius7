using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Reusable representative scenarios modelled on the handoff's reference run. Kept in one place so
/// Plans 2 &amp; 3 exercise the same shapes the fidelity guard validates.</summary>
public static class CanonicalScenarios
{
    public static ArchiveScenario RepresentativeArchive() => new(
        Events:
        [
            new ScanCompleteEvent(TotalFiles: 3122, TotalBytes: 3_160_000_000),
            new FileScannedEvent(RelativePath.Parse("big.bin"), 100_000_000),
            new FileHashingEvent(RelativePath.Parse("big.bin"), 100_000_000),
            new ChunkUploadedEvent(ChunkHash.Parse(new string('a', 64)), StoredSize: 60_000_000, OriginalSize: 100_000_000),
            new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 48_000_000),
            new SnapshotCreatedEvent(default, DateTimeOffset.UnixEpoch, 3122),
        ],
        Result: new ArchiveResult
        {
            Success = true, FilesScanned = 3122, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1,
            OriginalSize = 3_160_000_000, IncrementalSize = 100_000_000, IncrementalStoredSize = 60_000_000,
            FastHashReused = 0, FastHashRehashed = 3122, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
        });

    public static RestoreScenario RehydratingRestore() => new(
        PreCostEvents:
        [
            new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
            new TreeTraversalCompleteEvent(FileCount: 3122, TotalOriginalSize: 3_160_000_000),
            new ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000),
            new RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0),
        ],
        CostPrompt: new RestoreCostEstimate
        {
            ChunksAvailable = 145, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 282, ChunksPendingRehydration = 0,
            BytesNeedingRehydration = 2_100_000_000, BytesPendingRehydration = 0, DownloadBytes = 2_760_000_000,
            TotalStandard = 0.71, TotalHigh = 4.31, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
        },
        PostApproveEvents:
        [
            new RehydrationStatusEvent(Available: 145, Rehydrated: 282, NeedsRehydration: 0, Pending: 0),
            new FileRestoredEvent(RelativePath.Parse("big.bin"), 100_000_000),
        ],
        Result: new RestoreResult { Success = true, FilesRestored = 3122, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null });
}
