using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
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

        var result = await svc.LookupAsync([hash]);

        result.ContainsKey(hash).ShouldBeFalse();
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
        var result = await svc2.LookupAsync([contentHash]);

        result.ContainsKey(contentHash).ShouldBeTrue();
        result[contentHash].ChunkHash.ShouldBe("chunk-hash-001");
    }

    // ── Dedup: in-flight set prevents double-counting ─────────────────────────

    [Test]
    public async Task InFlightEntry_FoundWithoutBlob_ReturnsEntry()
    {
        var (svc, _) = await CreateServiceAsync();
        var contentHash = "ccddee00" + new string('2', 56);
        var entry       = new ShardEntry(contentHash, "some-chunk", 500, 200);

        svc.RecordEntry(entry); // goes to in-flight, NOT yet uploaded

        var result = await svc.LookupAsync([contentHash]);

        result.ContainsKey(contentHash).ShouldBeTrue();
        result[contentHash].ChunkHash.ShouldBe("some-chunk");
    }
}
