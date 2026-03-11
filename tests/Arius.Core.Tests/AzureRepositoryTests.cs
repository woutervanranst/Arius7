using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// AzureRepository unit tests — pure in-memory, no I/O
// ─────────────────────────────────────────────────────────────────────────────

public class AzureRepositoryTests
{
    private const string Passphrase = "test-passphrase";
    private const string WrongPassphrase = "wrong-passphrase";

    // ── Init ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Init_WritesConfigAndKeyBlobs()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);

        var (repoId, configBlobName, keyBlobName) = await repo.InitAsync(Passphrase);

        repoId.Value.ShouldNotBeNullOrEmpty();
        configBlobName.ShouldBe("config");
        keyBlobName.ShouldStartWith("keys/");

        storage.Contains("config").ShouldBeTrue();
        storage.Contains(keyBlobName).ShouldBeTrue();
    }

    [Test]
    public async Task Init_ConfigAndKeyBlobsAreInColdTier()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);

        var (_, configBlobName, keyBlobName) = await repo.InitAsync(Passphrase);

        storage.TierOf(configBlobName).ShouldBe(BlobAccessTier.Cold);
        storage.TierOf(keyBlobName).ShouldBe(BlobAccessTier.Cold);
    }

    [Test]
    public async Task Init_LoadConfig_ReturnsCorrectRepoId()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);

        var (repoId, _, _) = await repo.InitAsync(Passphrase);
        var loaded          = await repo.LoadConfigAsync();

        loaded.RepoId.ShouldBe(repoId);
    }

    // ── Key management ────────────────────────────────────────────────────────

    [Test]
    public async Task TryUnlock_CorrectPassphrase_ReturnsMasterKey()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var key = await repo.TryUnlockAsync(Passphrase);

        key.ShouldNotBeNull();
        key!.Length.ShouldBe(32);
    }

    [Test]
    public async Task TryUnlock_WrongPassphrase_ReturnsNull()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var key = await repo.TryUnlockAsync(WrongPassphrase);

        key.ShouldBeNull();
    }

    [Test]
    public async Task TryUnlock_SamePassphrase_ReturnsSameMasterKey()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var key1 = await repo.TryUnlockAsync(Passphrase);
        var key2 = await repo.TryUnlockAsync(Passphrase);

        key1.ShouldNotBeNull();
        key2.ShouldNotBeNull();
        key1!.SequenceEqual(key2!).ShouldBeTrue();
    }

    [Test]
    public async Task UnlockAsync_WrongPassphrase_Throws()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        await Should.ThrowAsync<InvalidOperationException>(() => repo.UnlockAsync(WrongPassphrase));
    }

    // ── Index ────────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteAndLoadIndex_RoundTrip()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var masterKey  = (await repo.TryUnlockAsync(Passphrase))!;
        var snapshotId = SnapshotId.New();
        var packId     = PackId.New();

        var entry = new IndexEntry(
            BlobHash.FromBytes([1, 2, 3], masterKey),
            packId,
            0,
            100,
            BlobType.Data);

        await repo.WriteIndexAsync(snapshotId, [entry]);

        var index = await repo.LoadIndexAsync();

        index.Count.ShouldBe(1);
        index.ContainsKey(entry.BlobHash.Value).ShouldBeTrue();
        index[entry.BlobHash.Value].PackId.ShouldBe(packId);
    }

    [Test]
    public async Task LoadIndex_MultipleFiles_MergesAll()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var masterKey = (await repo.TryUnlockAsync(Passphrase))!;
        var packId    = PackId.New();

        // Write two separate index files
        var snap1 = SnapshotId.New();
        var snap2 = SnapshotId.New();

        var e1 = new IndexEntry(BlobHash.FromBytes([1], masterKey), packId, 0,  50, BlobType.Data);
        var e2 = new IndexEntry(BlobHash.FromBytes([2], masterKey), packId, 50, 50, BlobType.Data);

        await repo.WriteIndexAsync(snap1, [e1]);
        await repo.WriteIndexAsync(snap2, [e2]);

        var index = await repo.LoadIndexAsync();

        index.Count.ShouldBe(2);
        index.ContainsKey(e1.BlobHash.Value).ShouldBeTrue();
        index.ContainsKey(e2.BlobHash.Value).ShouldBeTrue();
    }

    [Test]
    public async Task IndexBlobs_AreInColdTier()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var masterKey  = (await repo.TryUnlockAsync(Passphrase))!;
        var snapshotId = SnapshotId.New();
        var entry      = new IndexEntry(BlobHash.FromBytes([9], masterKey), PackId.New(), 0, 10, BlobType.Data);

        await repo.WriteIndexAsync(snapshotId, [entry]);

        var indexBlobName = $"index/{snapshotId.Value}";
        storage.TierOf(indexBlobName).ShouldBe(BlobAccessTier.Cold);
    }

    // ── Snapshots ────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteAndListSnapshot_RoundTrip()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var snapshot = MakeSnapshot();
        var doc      = new BackupSnapshotDocument(snapshot, []);

        await repo.WriteSnapshotAsync(doc);

        var docs = new List<BackupSnapshotDocument>();
        await foreach (var d in repo.ListSnapshotDocumentsAsync())
            docs.Add(d);

        docs.Count.ShouldBe(1);
        docs[0].Snapshot.Id.ShouldBe(snapshot.Id);
    }

    [Test]
    public async Task LoadSnapshot_ByFullId_Works()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var snapshot = MakeSnapshot();
        await repo.WriteSnapshotAsync(new BackupSnapshotDocument(snapshot, []));

        var loaded = await repo.LoadSnapshotDocumentAsync(snapshot.Id.Value);

        loaded.Snapshot.Id.ShouldBe(snapshot.Id);
    }

    [Test]
    public async Task LoadSnapshot_ByPrefix_Works()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var snapshot = MakeSnapshot();
        await repo.WriteSnapshotAsync(new BackupSnapshotDocument(snapshot, []));

        var prefix = snapshot.Id.Value[..8];
        var loaded = await repo.LoadSnapshotDocumentAsync(prefix);

        loaded.Snapshot.Id.ShouldBe(snapshot.Id);
    }

    [Test]
    public async Task LoadSnapshot_UnknownId_Throws()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        await Should.ThrowAsync<InvalidOperationException>(
            () => repo.LoadSnapshotDocumentAsync("nonexistent"));
    }

    [Test]
    public async Task SnapshotBlobs_AreInColdTier()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var snapshot = MakeSnapshot();
        await repo.WriteSnapshotAsync(new BackupSnapshotDocument(snapshot, []));

        storage.TierOf($"snapshots/{snapshot.Id.Value}").ShouldBe(BlobAccessTier.Cold);
    }

    // ── Trees ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteAndReadTree_RoundTrip()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var nodes = new List<TreeNode>
        {
            new("file.txt", TreeNodeType.File, 1024, DateTimeOffset.UtcNow, "644", [], null)
        };
        var hash = AzureRepository.ComputeTreeHash(nodes);

        await repo.WriteTreeAsync(hash, nodes);

        var loaded = await repo.ReadTreeAsync(hash);

        loaded.Count.ShouldBe(1);
        loaded[0].Name.ShouldBe("file.txt");
        loaded[0].Size.ShouldBe(1024);
    }

    [Test]
    public async Task TreeBlobs_AreInColdTier()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        await repo.InitAsync(Passphrase);

        var nodes = new List<TreeNode>
        {
            new("dir", TreeNodeType.Directory, 0, DateTimeOffset.UtcNow, "755", [], null)
        };
        var hash = AzureRepository.ComputeTreeHash(nodes);
        await repo.WriteTreeAsync(hash, nodes);

        storage.TierOf($"trees/{hash.Value}").ShouldBe(BlobAccessTier.Cold);
    }

    [Test]
    public void ComputeTreeHash_SameNodes_SameHash()
    {
        var nodes = new List<TreeNode>
        {
            new("a.txt", TreeNodeType.File, 100, DateTimeOffset.UnixEpoch, "644", [], null)
        };

        var hash1 = AzureRepository.ComputeTreeHash(nodes);
        var hash2 = AzureRepository.ComputeTreeHash(nodes);

        hash1.ShouldBe(hash2);
    }

    [Test]
    public void ComputeTreeHash_DifferentNodes_DifferentHash()
    {
        var nodes1 = new List<TreeNode>
        {
            new("a.txt", TreeNodeType.File, 100, DateTimeOffset.UnixEpoch, "644", [], null)
        };
        var nodes2 = new List<TreeNode>
        {
            new("b.txt", TreeNodeType.File, 200, DateTimeOffset.UnixEpoch, "644", [], null)
        };

        AzureRepository.ComputeTreeHash(nodes1).ShouldNotBe(AzureRepository.ComputeTreeHash(nodes2));
    }

    // ── Data packs ────────────────────────────────────────────────────────────

    [Test]
    public async Task UploadAndDownloadPack_RoundTrip()
    {
        var storage   = new InMemoryBlobStorageProvider();
        var repo      = new AzureRepository(storage);
        var packBytes = new byte[] { 1, 2, 3, 4, 5 };
        var packId    = PackId.New();
        var pack      = new SealedPack(packId, packBytes, []);

        await repo.UploadPackAsync(pack, BlobAccessTier.Archive);

        var downloaded = await repo.DownloadPackAsync(packId);

        downloaded.ShouldBe(packBytes);
    }

    [Test]
    public async Task UploadPack_UsesCallerSpecifiedTier()
    {
        var storage = new InMemoryBlobStorageProvider();
        var repo    = new AzureRepository(storage);
        var pack    = new SealedPack(PackId.New(), [0xFF], []);

        await repo.UploadPackAsync(pack, BlobAccessTier.Hot);

        var blobName = $"data/{pack.PackId.Value[..2]}/{pack.PackId.Value}";
        storage.TierOf(blobName).ShouldBe(BlobAccessTier.Hot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Snapshot MakeSnapshot() =>
        new(SnapshotId.New(),
            DateTimeOffset.UtcNow,
            TreeHash.Empty,
            ["/some/path"],
            "test-host",
            "test-user",
            [],
            null);
}
