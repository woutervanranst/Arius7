using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.ChunkIndex;

/// <summary>
/// Integration tests for <see cref="ChunkIndexService"/> tiered lookup through Azurite.
/// Requires Docker (Azurite via TestContainers). Task 4.11.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ChunkIndexServiceIntegrationTests(AzuriteFixture azurite)
{
    private const string Account   = "devstoreaccount1";
    private const string Passphrase = "test-passphrase";

    private async Task<(ChunkIndexService service, string tempDir)> CreateServiceAsync()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var containerName = container.Name;
        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Override L2 path by creating service pointing at temp dir
        // (ChunkIndexService uses ~/.arius/cache/<repoId>/chunk-index by default,
        // but for tests we patch via a subclass or just use a very distinct repoId)
        var svc = new ChunkIndexService(blobs, new PassphraseEncryptionService(Passphrase),
            Account, containerName);
        return (svc, tempDir);
    }

    // ── L3 miss (new prefix) → empty shard ───────────────────────────────────

    [Test]
    public async Task Lookup_NewPrefix_ReturnsEmpty()
    {
        var (svc, _) = await CreateServiceAsync();
        var hash = "aabbccdd" + new string('0', 56); // 64-char hash

        var result = await svc.LookupAsync(hash);

        result.ShouldBeNull();
    }

    // ── Record → Flush → new service instance → L3 hit ───────────────────────

    [Test]
    public async Task RecordAndFlush_ThenLookupInNewInstance_FindsEntry()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var encryption = new PassphraseEncryptionService(Passphrase);
        var containerName = container.Name;

        var svc1 = new ChunkIndexService(blobs, encryption, Account, containerName);

        var contentHash = "aabbccdd" + new string('1', 56);
        var entry       = new ShardEntry(contentHash, "chunk-hash-001", 1024, 512);
        svc1.RecordEntry(entry);
        await svc1.FlushAsync();

        // New service instance (L1 cold, L2 may have data)
        var svc2   = new ChunkIndexService(blobs, encryption, Account, containerName);
        var result = await svc2.LookupAsync(contentHash);

        result.ShouldNotBeNull();
        result.ChunkHash.ShouldBe("chunk-hash-001");
    }

    // ── Dedup: in-flight set prevents double-counting ─────────────────────────

    [Test]
    public async Task InFlightEntry_FoundWithoutBlob_ReturnsEntry()
    {
        var (svc, _) = await CreateServiceAsync();
        var contentHash = "ccddee00" + new string('2', 56);
        var entry       = new ShardEntry(contentHash, "some-chunk", 500, 200);

        svc.RecordEntry(entry); // goes to in-flight, NOT yet uploaded

        var result = await svc.LookupAsync(contentHash);

        result.ShouldNotBeNull();
        result.ChunkHash.ShouldBe("some-chunk");
    }

    // ── Stale L2 file (old encrypted bytes) → cache miss → L3 fallthrough ───────

    [Test]
    public async Task StaleL2File_IsTreatedAsCacheMiss_AndRefetchedFromL3()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var encryption    = new PassphraseEncryptionService(Passphrase);
        var containerName = container.Name;

        // Step 1: record + flush a real entry so the shard exists in L3
        var svc1        = new ChunkIndexService(blobs, encryption, Account, containerName);
        var contentHash = "ddee1122" + new string('3', 56);
        var entry       = new ShardEntry(contentHash, "stale-test-chunk", 800, 400);
        svc1.RecordEntry(entry);
        await svc1.FlushAsync();

        // Step 2: overwrite the L2 cache file with garbage (simulates old encrypted bytes)
        var prefix = Shard.PrefixOf(contentHash);
        var l2Path = Path.Combine(RepositoryPaths.GetChunkIndexCacheDirectory(Account, containerName), prefix);
        await File.WriteAllBytesAsync(l2Path, [0x53, 0x61, 0x6C, 0x74, 0x65, 0x64, 0x5F, 0x5F, 0xFF, 0xFE]); // "Salted__" + garbage

        // Step 3: new service instance with cold L1 — L2 hit fails, must fall through to L3
        var svc2   = new ChunkIndexService(blobs, encryption, Account, containerName);
        var result = await svc2.LookupAsync(contentHash);

        // The entry must still be found (came from L3)
        result.ShouldNotBeNull();
        result.ChunkHash.ShouldBe("stale-test-chunk");

        // And L2 must now be in plaintext format (re-cached by the service)
        File.Exists(l2Path).ShouldBeTrue();
        var text = await File.ReadAllTextAsync(l2Path);
        text.ShouldContain(contentHash);
    }
}
