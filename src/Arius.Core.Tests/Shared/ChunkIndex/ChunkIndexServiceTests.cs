using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Tests.Shared.ChunkIndex.Fakes;
using System.Collections.Concurrent;
using TUnit.Core;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task FlushAsync_MultiplePrefixes_UsesParallelUploads()
    {
        const string account = "acct-ci-parallel";
        var container = $"ctr-ci-parallel-{Guid.NewGuid():N}";
        CleanupRepo(account, container);

        try
        {
            var blobs = new RecordingChunkIndexBlobContainerService(TimeSpan.FromMilliseconds(150));
            var service = new ChunkIndexService(blobs, s_encryption, account, container);

            service.AddEntry(new ShardEntry("aaaa" + new string('1', 60), "chunk-a", 10, 5));
            service.AddEntry(new ShardEntry("bbbb" + new string('2', 60), "chunk-b", 20, 8));
            service.AddEntry(new ShardEntry("cccc" + new string('3', 60), "chunk-c", 30, 12));

            await service.FlushAsync();

            blobs.MaxConcurrentChunkIndexUploads.ShouldBeGreaterThan(1);
        }
        finally
        {
            CleanupRepo(account, container);
        }
    }

    [Test]
    public async Task FlushAsync_UpdatesL1AndL2Caches_AndMergesExistingShardContent()
    {
        const string account = "acct-ci-cache";
        var container = $"ctr-ci-cache-{Guid.NewGuid():N}";
        CleanupRepo(account, container);

        try
        {
            var blobs = new RecordingChunkIndexBlobContainerService();
            var seedService = new ChunkIndexService(blobs, s_encryption, account, container);
            var existingEntry = new ShardEntry("aaaa" + new string('0', 60), "chunk-existing", 100, 50);
            seedService.AddEntry(existingEntry);
            await seedService.FlushAsync();

            CleanupRepo(account, container);

            var service = new ChunkIndexService(blobs, s_encryption, account, container);
            var newEntry = new ShardEntry("aaaa" + new string('1', 60), "chunk-new", 200, 75);
            service.AddEntry(newEntry);

            await service.FlushAsync();

            var metadataReadsBeforeL1 = blobs.ChunkIndexMetadataReads;
            var downloadsBeforeL1 = blobs.ChunkIndexDownloads;

            var mergedLookup = await service.LookupAsync(existingEntry.ContentHash);

            mergedLookup.ShouldNotBeNull();
            mergedLookup.ChunkHash.ShouldBe(existingEntry.ChunkHash);
            blobs.ChunkIndexMetadataReads.ShouldBe(metadataReadsBeforeL1);
            blobs.ChunkIndexDownloads.ShouldBe(downloadsBeforeL1);

            var l2Path = Path.Combine(RepositoryPaths.GetChunkIndexCacheDirectory(account, container), Shard.PrefixOf(existingEntry.ContentHash));
            File.Exists(l2Path).ShouldBeTrue();

            var metadataReadsBeforeL2 = blobs.ChunkIndexMetadataReads;
            var downloadsBeforeL2 = blobs.ChunkIndexDownloads;
            var newService = new ChunkIndexService(blobs, s_encryption, account, container);

            var l2Lookup = await newService.LookupAsync(newEntry.ContentHash);

            l2Lookup.ShouldNotBeNull();
            l2Lookup.ChunkHash.ShouldBe(newEntry.ChunkHash);
            blobs.ChunkIndexMetadataReads.ShouldBe(metadataReadsBeforeL2);
            blobs.ChunkIndexDownloads.ShouldBe(downloadsBeforeL2);
        }
        finally
        {
            CleanupRepo(account, container);
        }
    }

    [Test]
    public async Task FlushAsync_ReportsProgress_PerCompletedPrefix()
    {
        const string account = "acct-ci-progress";
        var container = $"ctr-ci-progress-{Guid.NewGuid():N}";
        CleanupRepo(account, container);

        try
        {
            var blobs = new RecordingChunkIndexBlobContainerService();
            var service = new ChunkIndexService(blobs, s_encryption, account, container);
            var updates = new ConcurrentQueue<(int Completed, int Total)>();
            var progress = new SynchronousProgress<(int Completed, int Total)>(update => updates.Enqueue(update));

            service.AddEntry(new ShardEntry("aaaa" + new string('1', 60), "chunk-a", 10, 5));
            service.AddEntry(new ShardEntry("bbbb" + new string('2', 60), "chunk-b", 20, 8));
            service.AddEntry(new ShardEntry("cccc" + new string('3', 60), "chunk-c", 30, 12));

            await service.FlushAsync(progress);

            updates.Count.ShouldBe(3);
            updates.Select(u => u.Total).Distinct().ShouldBe([3]);
            updates.Select(u => u.Completed).OrderBy(x => x).ShouldBe([1, 2, 3]);
        }
        finally
        {
            CleanupRepo(account, container);
        }
    }

    [Test]
    public async Task FlushAsync_WhenPrefixUploadFails_RequeuesPendingEntries()
    {
        const string account = "acct-ci-requeue";
        var container = $"ctr-ci-requeue-{Guid.NewGuid():N}";
        CleanupRepo(account, container);

        try
        {
            var failingPrefix = "bbbb";
            var blobs = new RecordingChunkIndexBlobContainerService(failUploadForPrefix: failingPrefix);
            var service = new ChunkIndexService(blobs, s_encryption, account, container);

            var firstPrefixEntry = new ShardEntry("aaaa" + new string('1', 60), "chunk-a", 10, 5);
            var failingPrefixEntry = new ShardEntry(failingPrefix + new string('2', 60), "chunk-b", 20, 8);

            service.AddEntry(firstPrefixEntry);
            service.AddEntry(failingPrefixEntry);

            await Should.ThrowAsync<IOException>(() => service.FlushAsync());

            blobs.ClearFailure();

            await service.FlushAsync();

            var firstLookup = await service.LookupAsync(firstPrefixEntry.ContentHash);
            var secondLookup = await service.LookupAsync(failingPrefixEntry.ContentHash);

            firstLookup.ShouldNotBeNull();
            firstLookup.ChunkHash.ShouldBe(firstPrefixEntry.ChunkHash);
            secondLookup.ShouldNotBeNull();
            secondLookup.ChunkHash.ShouldBe(failingPrefixEntry.ChunkHash);
        }
        finally
        {
            CleanupRepo(account, container);
        }
    }

    private static void CleanupRepo(string account, string container)
    {
        var repoDir = RepositoryPaths.GetRepositoryDirectory(account, container);
        if (Directory.Exists(repoDir))
            Directory.Delete(repoDir, recursive: true);
    }

    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
                onReport(value);
        }
    }
}
