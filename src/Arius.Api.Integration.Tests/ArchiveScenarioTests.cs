using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ArchiveScenarioTests
{
    [Test]
    public async Task Scripted_archive_runs_to_completion_and_records_the_job()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(TotalFiles: 2, TotalBytes: 3000),
                new FileScannedEvent(RelativePath.Parse("a"), 2000),
                new FileHashingEvent(RelativePath.Parse("a"), 2000),
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), StoredSize: 400, OriginalSize: 2000),
                new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 1000),
                new SnapshotCreatedEvent(default, DateTimeOffset.UnixEpoch, 2),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 2, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1,
                OriginalSize = 3000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
                FastHashRehashed = 2, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunArchiveAsync(repoId, jobId, tier: "Archive", removeLocal: false, writePointers: false, fastHash: false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo("completed");
    }
}
