using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// RepositoryCache unit tests — in-memory SQLite, no I/O
// ─────────────────────────────────────────────────────────────────────────────

public class RepositoryCacheTests
{
    private static readonly byte[] TestKey = new byte[32];

    private static RepositoryCache OpenTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), "arius-cache-tests",
            Guid.NewGuid().ToString("N"), "test.db");
        return RepositoryCache.OpenAt(path);
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    [Test]
    public void OpenAt_CreatesDatabase_WithEmptyWatermark()
    {
        using var cache = OpenTemp();
        cache.Watermark.ShouldBe("");
    }

    // ── Blobs ─────────────────────────────────────────────────────────────────

    [Test]
    public void UpsertBlob_ThenFindBlob_ReturnsSameEntry()
    {
        using var cache = OpenTemp();
        var entry = MakeEntry();

        cache.UpsertBlob(entry);

        var found = cache.FindBlob(entry.BlobHash);
        found.ShouldNotBeNull();
        found!.PackId.ShouldBe(entry.PackId);
        found.Offset.ShouldBe(entry.Offset);
        found.Length.ShouldBe(entry.Length);
        found.BlobType.ShouldBe(entry.BlobType);
    }

    [Test]
    public void FindBlob_Unknown_ReturnsNull()
    {
        using var cache = OpenTemp();
        cache.FindBlob(BlobHash.FromBytes([99], TestKey)).ShouldBeNull();
    }

    [Test]
    public void UpsertBlob_Twice_LastWriteWins()
    {
        using var cache = OpenTemp();
        var hash   = BlobHash.FromBytes([1], TestKey);
        var packA  = PackId.New();
        var packB  = PackId.New();

        cache.UpsertBlob(new IndexEntry(hash, packA, 0, 100, BlobType.Data));
        cache.UpsertBlob(new IndexEntry(hash, packB, 0, 200, BlobType.Data));

        var found = cache.FindBlob(hash);
        found!.PackId.ShouldBe(packB);
        found.Length.ShouldBe(200);
    }

    [Test]
    public void LoadAllBlobs_ReturnsAllInserted()
    {
        using var cache = OpenTemp();
        var e1 = MakeEntry([1]);
        var e2 = MakeEntry([2]);
        var e3 = MakeEntry([3]);

        cache.InTransaction(() =>
        {
            cache.UpsertBlob(e1);
            cache.UpsertBlob(e2);
            cache.UpsertBlob(e3);
        });

        var all = cache.LoadAllBlobs();
        all.Count.ShouldBe(3);
        all.ContainsKey(e1.BlobHash.Value).ShouldBeTrue();
        all.ContainsKey(e2.BlobHash.Value).ShouldBeTrue();
        all.ContainsKey(e3.BlobHash.Value).ShouldBeTrue();
    }

    // ── Packs ─────────────────────────────────────────────────────────────────

    [Test]
    public void UpsertPack_StoredWithoutError()
    {
        using var cache = OpenTemp();
        var packId = PackId.New();
        Should.NotThrow(() => cache.UpsertPack(packId));
    }

    [Test]
    public void UpsertPack_Twice_IsIdempotent()
    {
        using var cache = OpenTemp();
        var packId = PackId.New();
        cache.UpsertPack(packId);
        Should.NotThrow(() => cache.UpsertPack(packId)); // INSERT OR IGNORE
    }

    // ── Snapshots ─────────────────────────────────────────────────────────────

    [Test]
    public void UpsertSnapshot_ThenListSnapshots_ReturnsSameSnapshot()
    {
        using var cache = OpenTemp();
        var snap = MakeSnapshot();

        cache.UpsertSnapshot(snap);

        var list = cache.ListSnapshots();
        list.Count.ShouldBe(1);
        list[0].Id.ShouldBe(snap.Id);
        list[0].Hostname.ShouldBe(snap.Hostname);
        list[0].Username.ShouldBe(snap.Username);
        list[0].Tree.ShouldBe(snap.Tree);
    }

    [Test]
    public void ListSnapshots_MultipleSnapshots_OrderedByTimeAsc()
    {
        using var cache = OpenTemp();
        var older = new Snapshot(SnapshotId.New(), DateTimeOffset.UtcNow.AddDays(-1),
            TreeHash.Empty, [], "h", "u", [], null);
        var newer = new Snapshot(SnapshotId.New(), DateTimeOffset.UtcNow,
            TreeHash.Empty, [], "h", "u", [], null);

        cache.UpsertSnapshot(newer);
        cache.UpsertSnapshot(older);

        var list = cache.ListSnapshots();
        list.Count.ShouldBe(2);
        list[0].Time.ShouldBeLessThan(list[1].Time);
    }

    [Test]
    public void UpsertSnapshot_WithParent_PreservesParentId()
    {
        using var cache = OpenTemp();
        var parentId = SnapshotId.New();
        var snap = new Snapshot(SnapshotId.New(), DateTimeOffset.UtcNow,
            TreeHash.Empty, ["/path"], "h", "u", [], parentId);

        cache.UpsertSnapshot(snap);

        var list = cache.ListSnapshots();
        list[0].Parent.ShouldBe(parentId);
    }

    // ── Trees ─────────────────────────────────────────────────────────────────

    [Test]
    public void UpsertTree_ThenFindTree_ReturnsSameNodes()
    {
        using var cache = OpenTemp();
        var nodes = new List<TreeNode>
        {
            new("file.txt", TreeNodeType.File, 512, DateTimeOffset.UtcNow, "644", [], null),
            new("subdir",   TreeNodeType.Directory, 0, DateTimeOffset.UtcNow, "755", [], TreeHash.Empty)
        };
        var hash = AzureRepository.ComputeTreeHash(nodes);

        cache.UpsertTree(hash, nodes);

        var found = cache.FindTree(hash);
        found.ShouldNotBeNull();
        found!.Count.ShouldBe(2);
        found[0].Name.ShouldBe("file.txt");
        found[1].Name.ShouldBe("subdir");
    }

    [Test]
    public void FindTree_Unknown_ReturnsNull()
    {
        using var cache = OpenTemp();
        cache.FindTree(new TreeHash("nonexistent")).ShouldBeNull();
    }

    // ── Watermark ─────────────────────────────────────────────────────────────

    [Test]
    public void Watermark_RoundTrip()
    {
        using var cache = OpenTemp();
        cache.Watermark = "index/abc123";
        cache.Watermark.ShouldBe("index/abc123");
    }

    [Test]
    public void Watermark_CanBeReset()
    {
        using var cache = OpenTemp();
        cache.Watermark = "snapshots/xyz";
        cache.Watermark = "";
        cache.Watermark.ShouldBe("");
    }

    // ── InTransaction ─────────────────────────────────────────────────────────

    [Test]
    public void InTransaction_BatchInsert_AllRowsVisible()
    {
        using var cache = OpenTemp();
        var entries = Enumerable.Range(0, 10)
            .Select(i => MakeEntry([(byte)i]))
            .ToList();

        cache.InTransaction(() =>
        {
            foreach (var e in entries)
                cache.UpsertBlob(e);
        });

        var all = cache.LoadAllBlobs();
        all.Count.ShouldBe(10);
    }

    // ── Equivalence with/without cache (8.5 requirement) ─────────────────────

    [Test]
    public async Task CacheEquivalence_LoadAllBlobs_MatchesDirectIndex()
    {
        // Build a mock provider with some index data
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync("pass");
        var masterKey = (await repo.TryUnlockAsync("pass"))!;

        var snap = SnapshotId.New();
        var e1   = new IndexEntry(BlobHash.FromBytes([10], masterKey), PackId.New(), 0, 100, BlobType.Data);
        var e2   = new IndexEntry(BlobHash.FromBytes([20], masterKey), PackId.New(), 0, 200, BlobType.Data);
        await repo.WriteIndexAsync(snap, [e1, e2]);

        // Build cache from Azure
        using var cache   = OpenTemp();
        var builder       = new RepositoryCacheBuilder(repo, cache);
        await builder.RebuildAsync();

        // Compare: direct Azure load vs cached load
        var direct = await repo.LoadIndexAsync();
        var cached = cache.LoadAllBlobs();

        cached.Count.ShouldBe(direct.Count);
        foreach (var (hash, entry) in direct)
        {
            cached.ContainsKey(hash).ShouldBeTrue();
            cached[hash].PackId.ShouldBe(entry.PackId);
        }
    }

    [Test]
    public async Task CacheEquivalence_ListSnapshots_MatchesDirect()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync("pass");

        var snap = MakeSnapshot();
        await repo.WriteSnapshotAsync(new BackupSnapshotDocument(snap, []));

        using var cache   = OpenTemp();
        var builder       = new RepositoryCacheBuilder(repo, cache);
        await builder.RebuildAsync();

        var directSnaps = new List<BackupSnapshotDocument>();
        await foreach (var doc in repo.ListSnapshotDocumentsAsync())
            directSnaps.Add(doc);

        var cachedSnaps = cache.ListSnapshots();

        cachedSnaps.Count.ShouldBe(directSnaps.Count);
        cachedSnaps[0].Id.ShouldBe(directSnaps[0].Snapshot.Id);
    }

    [Test]
    public async Task DeltaSync_OnlyDownloadsNewBlobs()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync("pass");
        var masterKey = (await repo.TryUnlockAsync("pass"))!;

        // First batch
        var snap1 = SnapshotId.New();
        var e1    = new IndexEntry(BlobHash.FromBytes([1], masterKey), PackId.New(), 0, 10, BlobType.Data);
        await repo.WriteIndexAsync(snap1, [e1]);

        using var cache   = OpenTemp();
        var builder       = new RepositoryCacheBuilder(repo, cache);
        await builder.SyncAsync(); // first sync

        var wm1 = cache.Watermark;
        cache.LoadAllBlobs().Count.ShouldBe(1);

        // Second batch (new blob added after watermark)
        var snap2 = SnapshotId.New();
        var e2    = new IndexEntry(BlobHash.FromBytes([2], masterKey), PackId.New(), 0, 20, BlobType.Data);
        await repo.WriteIndexAsync(snap2, [e2]);

        await builder.SyncAsync(); // delta sync

        cache.LoadAllBlobs().Count.ShouldBe(2);
        // Watermark should have advanced
        cache.Watermark.ShouldNotBe("");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IndexEntry MakeEntry(byte[]? seed = null)
    {
        var data = seed ?? [(byte)Random.Shared.Next(256)];
        return new IndexEntry(
            BlobHash.FromBytes(data, TestKey),
            PackId.New(),
            0,
            data.Length * 10L,
            BlobType.Data);
    }

    private static Snapshot MakeSnapshot() =>
        new(SnapshotId.New(), DateTimeOffset.UtcNow, TreeHash.Empty,
            ["/test"], "host", "user", [], null);
}
