using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.FakeTestHost;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class RepresentationScenarioTests
{
    [Test]
    public async Task Pointer_heavy_archive_reports_additive_new_bytes_not_underflow()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(TotalFiles: 1001, TotalBytes: 100_000_000),           // pointer-only files scanned as 0
                new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 1_000_000_000), // pointer-only dedup, full size
                new ChunkUploadingEvent(ChunkHash.Parse(new string('c', 64)), 100_000_000), // one new chunk queued
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), StoredSize: 60_000_000, OriginalSize: 100_000_000),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 1001, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1000,
                OriginalSize = 1_100_000_000, IncrementalSize = 100_000_000, IncrementalStoredSize = 60_000_000,
                FastHashReused = 0, FastHashRehashed = 1001, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunArchiveAsync(repoId, jobId, "Archive", false, false, false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId)!;
        await Assert.That(job.Status).IsEqualTo("completed");
        var snap = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson!)!.Snapshot;
        await Assert.That(snap.TotalNewBytes).IsEqualTo(100_000_000L);   // additive, not Max(0, 100M - 1000M)=0
    }

    [Test]
    public async Task Restore_reports_authoritative_chunk_total_including_needs_rehydration()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 100, TotalOriginalSize: 3_000_000),
                new ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000),
                new RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0),
            ],
            CostPrompt: null,   // nothing pending pre-cost in this snapshot shape; no prompt
            PostApproveEvents: [ new FileRestoredEvent(RelativePath.Parse("a"), 3_000_000) ],
            Result: new RestoreResult { Success = true, FilesRestored = 100, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId)!;
        // With CostPrompt null and no pending rehydration the restore completes without resetting the sink's
        // chunk total, so it survives unchanged into the terminal persisted snapshot. That makes state_json the
        // reliable place to assert ChunksTotal == 427 (the authoritative total including chunks still needing
        // rehydration), not just a live/in-flight value.
        await Assert.That(job!.Status).IsEqualTo("completed");
        var snap = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson!)!.Snapshot;
        await Assert.That(snap.ChunksTotal).IsEqualTo(427);   // includes the 282 needing rehydration
    }
}
