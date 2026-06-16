using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Data.Sqlite;

namespace Arius.Integration.Tests.ChunkIndex;

/// <summary>
/// Integration tests for <see cref="ChunkIndexService"/> tiered lookup through Azurite.
/// Requires Docker (Azurite via TestContainers). Task 4.11.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ChunkIndexServiceIntegrationTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";

    private async Task<(ChunkIndexService service, LocalDirectory tempDir)> CreateServiceAsync()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var containerName = container.Name;
        var tempDir       = TestTempRoots.CreateDirectory("test");
        new RelativeFileSystem(tempDir).CreateDirectory(RelativePath.Root);

        // Override L2 path by creating service pointing at temp dir
        // (ChunkIndexService uses ~/.arius/cache/<repoId>/chunk-index by default,
        // but for tests we patch via a subclass or just use a very distinct repoId)
        var encryption = IEncryptionService.EncryptedInstance;
        var snapshot = new SnapshotService(blobs, encryption, TestCompression.Instance, Account, containerName);
        var svc = new ChunkIndexService(blobs, encryption, TestCompression.Instance, snapshot, Account, containerName);
        return (svc, tempDir);
    }

    // ── L3 miss (new prefix) → empty shard ───────────────────────────────────

    [Test]
    public async Task Lookup_NewPrefix_ReturnsEmpty()
    {
        var (svc, _) = await CreateServiceAsync();
        var hash = FakeContentHash('0');

        var result = await svc.LookupAsync(hash);

        result.ShouldBeNull();
    }

    // ── Record → Flush → new service instance → L3 hit ───────────────────────

    [Test]
    public async Task RecordAndFlush_ThenLookupInNewInstance_FindsEntry()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var encryption = IEncryptionService.EncryptedInstance;
        var containerName = container.Name;

        var snapshot = new SnapshotService(blobs, encryption, TestCompression.Instance, Account, containerName);
        var svc1 = new ChunkIndexService(blobs, encryption, TestCompression.Instance, snapshot, Account, containerName);

        var contentHash = FakeContentHash('1');
        var chunkHash   = FakeChunkHash('a');
        var entry       = new ShardEntry(contentHash, chunkHash, 1024, 512, BlobTier.Cool);
        svc1.AddEntry(entry);
        await svc1.FlushAsync();

        // New service instance (L1 cold, L2 may have data)
        var svc2   = new ChunkIndexService(blobs, encryption, TestCompression.Instance, snapshot, Account, containerName);
        var result = await svc2.LookupAsync(contentHash);

        result.ShouldNotBeNull();
        result.ChunkHash.ShouldBe(chunkHash);
    }

    // ── Dedup: in-flight set prevents double-counting ─────────────────────────

    [Test]
    public async Task InFlightEntry_FoundWithoutBlob_ReturnsEntry()
    {
        var (svc, _) = await CreateServiceAsync();
        var contentHash = FakeContentHash('2');
        var chunkHash   = FakeChunkHash('b');
        var entry       = new ShardEntry(contentHash, chunkHash, 500, 200, BlobTier.Cool);

        svc.AddEntry(entry); // goes to in-flight, NOT yet uploaded

        var result = await svc.LookupAsync(contentHash);

        result.ShouldNotBeNull();
        result.ChunkHash.ShouldBe(chunkHash);
    }

    // ── Corrupt local SQLite store → fail with recovery guidance ───────────────

    [Test]
    public async Task CorruptLocalStore_FailsWithRecoveryGuidance()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var encryption    = IEncryptionService.EncryptedInstance;
        var containerName = container.Name;

        // Step 1: record + flush a real entry so the shard exists in L3
        var snapshot = new SnapshotService(blobs, encryption, TestCompression.Instance, Account, containerName);
        var svc1        = new ChunkIndexService(blobs, encryption, TestCompression.Instance, snapshot, Account, containerName);
        var contentHash = FakeContentHash('3');
        var chunkHash   = FakeChunkHash('c');
        var entry       = new ShardEntry(contentHash, chunkHash, 800, 400, BlobTier.Cool);
        svc1.AddEntry(entry);
        await svc1.FlushAsync();

        // Step 2: create a new service instance with cold local state.
        var svc2 = new ChunkIndexService(blobs, encryption, TestCompression.Instance, snapshot, Account, containerName);

        // Step 3: corrupt the local SQLite cache so the next lookup must recover and refill it.
        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(Account, containerName);
        var databasePath = cacheRoot.Resolve(RelativePath.Parse("cache.sqlite"));
        var walPath = cacheRoot.Resolve(RelativePath.Parse("cache.sqlite-wal"));
        var shmPath = cacheRoot.Resolve(RelativePath.Parse("cache.sqlite-shm"));
        ClearPool(Account, containerName);
        if (File.Exists(walPath))
            File.Delete(walPath);
        if (File.Exists(shmPath))
            File.Delete(shmPath);

        await File.WriteAllBytesAsync(databasePath, [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62]); // "not-a-db"

        var ex = await Should.ThrowAsync<ChunkIndexLocalStoreException>(() => svc2.LookupAsync(contentHash));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
        ex.Message.ShouldContain("repair command");
    }

    private static void ClearPool(string accountName, string containerName)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName).Resolve(RelativePath.Parse("cache.sqlite")),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString());

        SqliteConnection.ClearPool(connection);
    }
}
