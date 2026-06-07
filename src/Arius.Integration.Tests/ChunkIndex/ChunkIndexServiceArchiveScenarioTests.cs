using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Integration.Tests.ChunkIndex.Fakes;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.ChunkIndex;

/// <summary>
/// Azurite-backed scenario tests that prove the <see cref="ChunkIndexService"/> sync protocol works against real
/// Azure ETag / overwrite / round-trip semantics (not just the in-memory Fake's model of them). The exhaustive
/// per-path matrix and snapshot-version control live in the fast Fake-based suite; these focus on the headline
/// cross-machine flows. Requires Docker (Azurite via TestContainers); skips gracefully when unavailable.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ChunkIndexServiceArchiveScenarioTests(AzuriteFixture azurite)
{
    private const string Account    = "devstoreaccount1";
    private const string Passphrase = "test-passphrase";

    // ── First run uploads shards; another machine (empty cache) downloads each touched prefix once ─────────────

    [Test]
    public async Task FirstRun_ThenAnotherMachine_DownloadsEachPrefixOnce_AndFindsEntries()
    {
        var (container, raw) = await azurite.CreateTestServiceAsync();
        var encryption    = new PassphraseEncryptionService(Passphrase);
        var containerName = container.Name;

        // Two chunks in prefix of h1, one in another prefix.
        var h1  = FakeContentHash('1');
        var h1b = SamePrefix(h1, 'a');
        var h2  = FakeContentHash('2');
        var e1  = new ShardEntry(h1,  FakeChunkHash('a'), 1024, 512);
        var e1b = new ShardEntry(h1b, FakeChunkHash('b'), 2048, 700);
        var e2  = new ShardEntry(h2,  FakeChunkHash('c'), 4096, 900);

        // Machine A: archive the three chunks and flush, writing two shards.
        var counterA  = new CountingBlobContainerService(raw);
        var snapshotA = new SnapshotService(raw, encryption, Account, containerName);
        using (var machineA = new ChunkIndexService(counterA, encryption, snapshotA, Account, containerName))
        {
            (await machineA.LookupAsync(h1)).ShouldBeNull();
            (await machineA.LookupAsync(h1b)).ShouldBeNull();
            (await machineA.LookupAsync(h2)).ShouldBeNull();
            machineA.AddEntry(e1);
            machineA.AddEntry(e1b);
            machineA.AddEntry(e2);
            await machineA.FlushAsync();
        }
        counterA.ChunkIndexUploads.ShouldBe(2); // one shard per touched prefix

        // Machine B: a different local cache identity over the SAME remote container — its cache is empty.
        var counterB  = new CountingBlobContainerService(raw);
        var snapshotB = new SnapshotService(raw, encryption, Account, containerName);
        using var machineB = new ChunkIndexService(counterB, encryption, snapshotB, Account, $"{containerName}-b");

        (await machineB.LookupAsync(h1)).ShouldBe(e1);   // real download + deserialize from Azure
        (await machineB.LookupAsync(h1b)).ShouldBe(e1b); // same prefix → served from the loaded shard
        (await machineB.LookupAsync(h2)).ShouldBe(e2);   // real download of the second prefix

        counterB.ChunkIndexTryDownloads.ShouldBe(2); // one download per touched prefix, not per hash
        counterB.ChunkIndexUploads.ShouldBe(0);
    }

    // ── Second run on the same machine: warm local cache → no chunk-index downloads ───────────────────────────

    [Test]
    public async Task SecondRunSameMachine_WarmCache_MakesNoChunkIndexDownloads()
    {
        var (container, raw) = await azurite.CreateTestServiceAsync();
        var encryption    = new PassphraseEncryptionService(Passphrase);
        var containerName = container.Name;
        var snapshot      = new SnapshotService(raw, encryption, Account, containerName);

        var h1 = FakeContentHash('3');
        var e1 = new ShardEntry(h1, FakeChunkHash('c'), 800, 400);

        using (var run1 = new ChunkIndexService(raw, encryption, snapshot, Account, containerName))
        {
            await run1.LookupAsync(h1);
            run1.AddEntry(e1);
            await run1.FlushAsync();
        }

        // A new instance with the SAME (account, container) reuses the persisted local cache.
        var counter = new CountingBlobContainerService(raw);
        using var run2 = new ChunkIndexService(counter, encryption, snapshot, Account, containerName);

        (await run2.LookupAsync(h1)).ShouldBe(e1); // served from the warm local cache (no remote read)

        counter.ChunkIndexTryDownloads.ShouldBe(0);
        counter.ChunkIndexUploads.ShouldBe(0);
    }

    /// <summary>A content hash sharing <paramref name="hash"/>'s shard prefix but filled with <paramref name="fill"/>.</summary>
    private static ContentHash SamePrefix(ContentHash hash, char fill)
        => ContentHash.Parse($"{hash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string(fill, 64 - ChunkIndexService.ShardPrefixLength)}");
}
