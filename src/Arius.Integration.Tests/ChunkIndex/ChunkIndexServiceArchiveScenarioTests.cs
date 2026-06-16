using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.ChunkIndex.Fakes;
using Arius.Tests.Shared;
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
    private const string Account = "devstoreaccount1";

    // ── First run uploads shards; another machine (empty cache) downloads each touched prefix once ─────────────

    [Test]
    public async Task FirstRun_ThenAnotherMachine_DownloadsEachPrefixOnce_AndFindsEntries()
    {
        var (container, raw) = await azurite.CreateTestServiceAsync();
        var encryption    = IEncryptionService.EncryptedInstance;
        var containerName = container.Name;

        // Two chunks in prefix of h1, one in another prefix.
        var h1  = FakeContentHash('1');
        var h1b = SamePrefix(h1, 'a');
        var h2  = FakeContentHash('2');
        var e1  = new ShardEntry(h1,  FakeChunkHash('a'), 1024, 512, BlobTier.Cool);
        var e1b = new ShardEntry(h1b, FakeChunkHash('b'), 2048, 700, BlobTier.Cool);
        var e2  = new ShardEntry(h2,  FakeChunkHash('c'), 4096, 900, BlobTier.Cool);

        // Machine A: archive the three chunks and flush, writing two shards.
        var counterA  = new CountingBlobContainerService(raw);
        var snapshotA = new SnapshotService(raw, encryption, ICompressionService.ZtdInstance, Account, containerName);
        using (var machineA = new ChunkIndexService(counterA, encryption, ICompressionService.ZtdInstance, snapshotA, Account, containerName))
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
        var snapshotB = new SnapshotService(raw, encryption, ICompressionService.ZtdInstance, Account, containerName);
        using var machineB = new ChunkIndexService(counterB, encryption, ICompressionService.ZtdInstance, snapshotB, Account, $"{containerName}-b");

        (await machineB.LookupAsync(h1)).ShouldBe(e1);   // real download + deserialize from Azure
        (await machineB.LookupAsync(h1b)).ShouldBe(e1b); // same prefix → served from the loaded shard
        (await machineB.LookupAsync(h2)).ShouldBe(e2);   // real download of the second prefix

        counterB.ChunkIndexTryDownloads.ShouldBe(2); // one download per touched prefix, not per hash
        counterB.ChunkIndexUploads.ShouldBe(0);
        counterB.ChunkIndexLists.ShouldBe(2);        // one subtree listing per touched root
    }

    // ── Shard split: a later run splits the shard; entries from earlier runs stay resolvable ──────────────────

    [Test]
    public async Task SplitRoundtrip_SecondMachineResolvesEntriesFromEarlierRunsThroughSplitLayout()
    {
        // Proves the dynamic layout against REAL Azure semantics: raw name-prefix listing (no
        // trailing-slash segment alignment), child uploads, parent delete, and the parent-wins walk
        // on a cold machine. The first run's entries play the role of chunks only referenced by
        // older snapshots (e.g. files since deleted locally) — a later split must keep them reachable.
        var (container, raw) = await azurite.CreateTestServiceAsync();
        var containerName = container.Name;

        var h1 = FakeContentHash('4');
        var h2 = SamePrefix(h1, 'a');
        var entriesRun1 = new[]
        {
            new ShardEntry(h1, FakeChunkHash('a'), 1024, 512, BlobTier.Cool),
            new ShardEntry(h2, FakeChunkHash('b'), 2048, 700, BlobTier.Cool),
        };
        var entriesRun2 = new[] { 'c', 'd', 'e' }
            .Select(fill => new ShardEntry(SamePrefix(h1, fill), FakeChunkHash(fill), 100, 50, BlobTier.Cool))
            .ToArray();
        var rootPrefix = ChunkIndexRouter.GetRootPrefix(h1);

        // Run 1 (machine A): two entries fit one root shard.
        var snapshotA = new SnapshotService(raw, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance, Account, containerName);
        using (var run1 = new ChunkIndexService(raw, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance, snapshotA, Account, containerName, maxShardEntryCount: 3))
        {
            foreach (var entry in entriesRun1)
                run1.AddEntry(entry);
            await run1.FlushAsync();
        }

        // Run 2 (machine A): three more entries push the shard past the threshold → split.
        using (var run2 = new ChunkIndexService(raw, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance, snapshotA, Account, containerName, maxShardEntryCount: 3))
        {
            foreach (var entry in entriesRun2)
                run2.AddEntry(entry);
            await run2.FlushAsync();
        }

        // The parent shard is gone; only deeper (non-empty) leaves remain in the subtree.
        var subtree = new List<string>();
        await foreach (var item in raw.ListAsync(BlobPaths.ChunkIndexPrefix / rootPrefix, BlobListPrefixKind.BlobNamePrefix))
            subtree.Add(item.Name.Name.ToString());
        subtree.ShouldNotContain(rootPrefix.ToString());
        subtree.ShouldNotBeEmpty();
        subtree.ShouldAllBe(name => name.Length > rootPrefix.ToString().Length);

        // Machine B (cold cache) resolves every entry — including run 1's — through the split layout.
        var counterB  = new CountingBlobContainerService(raw);
        var snapshotB = new SnapshotService(raw, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance, Account, containerName);
        using var machineB = new ChunkIndexService(counterB, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance, snapshotB, Account, $"{containerName}-b");
        var allEntries = entriesRun1.Concat(entriesRun2).ToArray();
        var resolved = await machineB.LookupAsync(allEntries.Select(entry => entry.ContentHash));
        foreach (var entry in allEntries)
            resolved[entry.ContentHash].ShouldBe(entry);

        counterB.ChunkIndexLists.ShouldBe(1); // the whole subtree resolved from one root listing
        counterB.ChunkIndexUploads.ShouldBe(0);
    }

    // ── Second run on the same machine: warm local cache → no chunk-index downloads ───────────────────────────

    [Test]
    public async Task SecondRunSameMachine_WarmCache_MakesNoChunkIndexDownloads()
    {
        var (container, raw) = await azurite.CreateTestServiceAsync();
        var encryption    = IEncryptionService.EncryptedInstance;
        var containerName = container.Name;
        var snapshot      = new SnapshotService(raw, encryption, ICompressionService.ZtdInstance, Account, containerName);

        var h1 = FakeContentHash('3');
        var e1 = new ShardEntry(h1, FakeChunkHash('c'), 800, 400, BlobTier.Cool);

        using (var run1 = new ChunkIndexService(raw, encryption, ICompressionService.ZtdInstance, snapshot, Account, containerName))
        {
            await run1.LookupAsync(h1);
            run1.AddEntry(e1);
            await run1.FlushAsync();
        }

        // A new instance with the SAME (account, container) reuses the persisted local cache.
        var counter = new CountingBlobContainerService(raw);
        using var run2 = new ChunkIndexService(counter, encryption, ICompressionService.ZtdInstance, snapshot, Account, containerName);

        (await run2.LookupAsync(h1)).ShouldBe(e1); // served from the warm local cache (no remote read)

        counter.ChunkIndexTryDownloads.ShouldBe(0);
        counter.ChunkIndexUploads.ShouldBe(0);
    }

    /// <summary>A content hash sharing <paramref name="hash"/>'s shard prefix but filled with <paramref name="fill"/>.</summary>
    private static ContentHash SamePrefix(ContentHash hash, char fill)
        => ContentHash.Parse($"{hash.Prefix(ChunkIndexService.MinShardPrefixLength)}{new string(fill, 64 - ChunkIndexService.MinShardPrefixLength)}");
}
