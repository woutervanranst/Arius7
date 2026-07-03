using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.HashCache;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class ArchiveFastHashTests
{
    // A few MB so the file is routed as a "large" file and fully hashed on the cold run.
    private const int LargeFileSize = 3 * 1024 * 1024;

    [Test]
    public async Task SecondRun_WithFastHash_ReusesHashes_NoRehash()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("large.bin"), LargeFileSize);

        // ── Run 1: cold cache → success, populates the hashcache.
        var first = await ArchiveAsync(fixture, new FakeLogger<ArchiveCommandHandler>(), fastHash: true);
        first.Success.ShouldBeTrue(first.ErrorMessage);
        first.FastHashReused.ShouldBe(0);    // cold cache: nothing reused
        first.FastHashRehashed.ShouldBe(1);  // one file fully hashed and recorded

        // ── Run 2: warm cache, unchanged files → reuse, no rehash.
        var secondLogger = new FakeLogger<ArchiveCommandHandler>();
        var second = await ArchiveAsync(fixture, secondLogger, fastHash: true);
        second.Success.ShouldBeTrue(second.ErrorMessage);
        second.FastHashReused.ShouldBe(1);   // warm cache: one file served from cache
        second.FastHashRehashed.ShouldBe(0); // no full read required

        var messages = secondLogger.Collector.GetSnapshot().Select(r => r.Message).ToArray();

        // Reuse reason is platform-dependent ("ctime match" on Linux, "size+fp match" on a floor-only
        // platform), so assert on the per-file "-> reused" line rather than the specific reason.
        messages.ShouldContain(m => m.Contains("[fast-hash]") && m.Contains("-> reused"));
    }

    [Test]
    [NotInParallel]
    public async Task ThirdRun_AfterCacheDeleted_FullHashesAgain()
    {
        await using var fixture = await CreateArchiveFixtureAsync();
        await WriteRandomFileAsync(fixture, RelativePath.Parse("large.bin"), LargeFileSize);

        // Cold run → populate.
        var firstRun = await ArchiveAsync(fixture, new FakeLogger<ArchiveCommandHandler>(), fastHash: true);
        firstRun.Success.ShouldBeTrue();
        firstRun.FastHashReused.ShouldBe(0);
        firstRun.FastHashRehashed.ShouldBe(1);
        // Warm run → reuse (sanity).
        var secondRun = await ArchiveAsync(fixture, new FakeLogger<ArchiveCommandHandler>(), fastHash: true);
        secondRun.Success.ShouldBeTrue();
        secondRun.FastHashReused.ShouldBe(1);
        secondRun.FastHashRehashed.ShouldBe(0);

        // Delete the hashcache root so the next run sees a cold cache again. The store opens pooled
        // SQLite connections, so clear the pool first — otherwise a pooled handle to the (now unlinked)
        // database keeps serving the old rows and the "cold" run would still see a warm cache.
        var hashCacheRootDir = RepositoryLocalStatePaths.GetHashCacheRoot(fixture.AccountName, fixture.ContainerName);
        ClearHashCachePool();
        var hashCacheRoot = hashCacheRootDir.ToString();
        if (Directory.Exists(hashCacheRoot))
            Directory.Delete(hashCacheRoot, recursive: true);

        var coldAgainLogger = new FakeLogger<ArchiveCommandHandler>();
        var third = await ArchiveAsync(fixture, coldAgainLogger, fastHash: true);
        third.Success.ShouldBeTrue(third.ErrorMessage);

        var messages = coldAgainLogger.Collector.GetSnapshot().Select(r => r.Message).ToArray();
        // No per-file reuse on a cold cache (match the per-file "-> reused" line, not the summary's "reused N").
        messages.ShouldNotContain(m => m.Contains("[fast-hash]") && m.Contains("-> reused"));
        third.FastHashReused.ShouldBe(0);   // cold cache: nothing reused
        third.FastHashRehashed.ShouldBe(1); // one file fully hashed and recorded
    }

    private static async Task<ArchiveResult> ArchiveAsync(
        RepositoryTestFixture fixture,
        FakeLogger<ArchiveCommandHandler> logger,
        bool fastHash)
    {
        // Build the handler directly so we control the logger we inspect, and a real hashcache so the
        // reuse path is genuinely exercised (not a mock). Mirrors ArchiveRecoveryTests' direct wiring.
        // A fresh chunk-index per run: the index is single-flush-per-lifetime, so re-archiving the same
        // repository requires a new index instance each call (same as the fixture's CreateArchiveHandler).
        using var index = new ChunkIndexService(
            fixture.BlobContainer, fixture.Encryption, fixture.Compression, fixture.Snapshot, fixture.AccountName, fixture.ContainerName);
        var hashCache = new HashCacheService(
            new HashCacheLocalStore(RepositoryLocalStatePaths.GetHashCacheRoot(fixture.AccountName, fixture.ContainerName)));

        var handler = new ArchiveCommandHandler(
            fixture.BlobContainer,
            fixture.Encryption,
            index,
            fixture.ChunkStorage,
            hashCache,
            fixture.FileTreeService,
            fixture.Snapshot,
            fixture.Mediator,
            logger,
            NullLoggerFactory.Instance,
            fixture.AccountName,
            fixture.ContainerName,
            FileExclusionFilter.None);

        return await handler.Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fixture.LocalDirectory.ToString(),
                UploadTier         = BlobTier.Cool,
                SmallFileThreshold = 1024 * 1024,
                FastHash           = fastHash,
            }),
            CancellationToken.None);
    }

    private static async ValueTask<RepositoryTestFixture> CreateArchiveFixtureAsync()
        => await RepositoryTestFixture.CreateWithEncryptionAsync(
            new FakeInMemoryBlobContainerService(),
            "test-account",
            $"test-container-{Guid.NewGuid():N}",
            IEncryptionService.PlaintextInstance,
            TestTempRoots.CreateDirectory("archive-fasthash-test"));

    private static async Task<byte[]> WriteRandomFileAsync(RepositoryTestFixture fixture, RelativePath relativePath, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Random.Shared.NextBytes(content);
        await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);
        return content;
    }

    private static void ClearHashCachePool()
    {
        // Clear every pooled SQLite handle: the hashcache store opens pooled connections and the exact
        // connection-string key is an implementation detail, so ClearAllPools is the robust way to release
        // the (now-unlinked) database before deleting it. Without this, a pooled handle keeps serving the
        // old rows and the "cold" run would still see a warm cache.
        SqliteConnection.ClearAllPools();
    }
}
