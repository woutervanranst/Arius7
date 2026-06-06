using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Tests.Shared.ChunkIndex.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceLookupTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task LookupAsync_MissingRemoteShard_ReturnsMiss()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var contentHash = FakeContentHash('a');
        using var index = CreateIndex(blobs, "missing");

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBeNull();
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash))]);
    }

    [Test]
    public async Task LookupAsync_CorruptRemoteShard_ThrowsChunkIndexCorruptException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var contentHash = FakeContentHash('a');
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, [1, 2, 3], BlobTier.Cool);
        using var index = CreateIndex(blobs, "corrupt");

        var ex = await Should.ThrowAsync<ChunkIndexCorruptException>(() => index.LookupAsync(contentHash));

        ex.Message.ShouldContain("Run the explicit chunk-index repair command");
        ex.ShardBlobName.ShouldBe(shardBlobName);
    }

    [Test]
    public async Task LookupAsync_CorruptLocalShard_DeletesLocalCacheAndReloadsRemoteShard()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("local-corrupt");
        var snapshot = new FakeSnapshotService();
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shard = CreateShard(entry);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(shard, s_encryption), BlobTier.Cool);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        cache.CreateDirectory(RelativePath.Root);
        await cache.WriteAllBytesAsync(RelativePath.Root / Shard.PrefixOf(contentHash), [1, 2, 3], CancellationToken.None);

        using var index = new ChunkIndexService(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBe(entry);
    }

    [Test]
    public async Task LookupAsync_RemoteShard_LoadsPrefixViaTryDownload()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("remote-no-metadata");
        var snapshot = new FakeSnapshotService();
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shard = CreateShard(entry);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)),
            await ShardSerializer.SerializeAsync(shard, s_encryption),
            BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBe(entry);
        blobs.RequestedBlobNames.ShouldContain(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)));
    }

    [Test]
    public async Task LookupAsync_ValidShardMissingEntry_ReturnsMissWithoutRepair()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var existingHash = FakeContentHash('a');
        var missingHash = ContentHash.Parse($"{existingHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('b', 64 - ChunkIndexService.ShardPrefixLength)}");
        var shard = CreateShard(new ShardEntry(existingHash, FakeChunkHash('c'), 10, 5));
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(missingHash)),
            await ShardSerializer.SerializeAsync(shard, s_encryption),
            BlobTier.Cool);
        using var index = CreateIndex(blobs, "valid-miss");

        var actual = await index.LookupAsync(missingHash);

        actual.ShouldBeNull();
        blobs.UploadedBlobNames.ShouldBeEmpty();
    }

    [Test]
    public async Task LookupAsync_MultipleHashes_ReturnsHitsAndOmitsMisses()
    {
        // Arrange
        var blobs            = new FakeInMemoryBlobContainerService();
        var firstHash        = FakeContentHash('a');
        var secondHash       = ContentHash.Parse($"{firstHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('b', 64 - ChunkIndexService.ShardPrefixLength)}");
        var missingHash      = ContentHash.Parse($"{firstHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('c', 64 - ChunkIndexService.ShardPrefixLength)}");
        var otherPrefixHash  = FakeContentHash('d');
        var inFlightHash     = FakeContentHash('e');
        var firstEntry       = new ShardEntry(firstHash,       FakeChunkHash('1'), 10, 5);
        var secondEntry      = new ShardEntry(secondHash,      FakeChunkHash('2'), 20, 8);
        var otherPrefixEntry = new ShardEntry(otherPrefixHash, FakeChunkHash('3'), 30, 12);
        var inFlightEntry    = new ShardEntry(inFlightHash,    FakeChunkHash('4'), 40, 16);
        
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash)),
            await ShardSerializer.SerializeAsync(CreateShard(firstEntry, secondEntry), s_encryption),
            BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherPrefixHash)),
            await ShardSerializer.SerializeAsync(CreateShard(otherPrefixEntry), s_encryption),
            BlobTier.Cool);
        using var index = CreateIndex(blobs, "multiple");
        index.AddEntry(inFlightEntry);

        // Act
        var actual = await index.LookupAsync([firstHash, secondHash, missingHash, otherPrefixHash, inFlightHash]);

        // Assert
        actual.Count.ShouldBe(4);
        actual[firstHash].ShouldBe(firstEntry);
        actual[secondHash].ShouldBe(secondEntry);
        actual[otherPrefixHash].ShouldBe(otherPrefixEntry);
        actual[inFlightHash].ShouldBe(inFlightEntry);
        actual.ShouldNotContainKey(missingHash);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash))).ShouldBe(1);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherPrefixHash))).ShouldBe(1);
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(inFlightHash)));
    }

    [Test]
    public async Task LookupAsync_LoadedPrefixForCurrentSnapshot_SkipsRepeatedTryDownloadValidation()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("lookup-skip-revalidate");
        var snapshot = new FakeSnapshotService();
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)),
            await ShardSerializer.SerializeAsync(CreateShard(entry), s_encryption),
            BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);

        (await index.LookupAsync(contentHash)).ShouldBe(entry);
        blobs.RequestedBlobNames.Clear();

        (await index.LookupAsync(contentHash)).ShouldBe(entry);

        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)));
    }

    [Test]
    public async Task LookupAsync_MissingRemoteShard_ClearsStaleCleanEntries_AndSkipsRepeatedValidation()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("lookup-missing-reset");
        var snapshot = new FakeSnapshotService();
        var contentHash = FakeContentHash('a');
        var prefix = Shard.PrefixOf(contentHash);
        var staleEntry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        using (var store = new ChunkIndexLocalStore(cacheRoot))
            store.UpdatePrefix(prefix, "remote-1", "snapshot-old", [staleEntry]);

        using var index = new ChunkIndexService(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);

        (await index.LookupAsync(contentHash)).ShouldBeNull();
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkIndexShardPath(prefix)]);
        blobs.RequestedBlobNames.Clear();

        (await index.LookupAsync(contentHash)).ShouldBeNull();

        blobs.RequestedBlobNames.ShouldBeEmpty();
    }

    [Test]
    public async Task LookupAsync_NewSnapshotWithSameRemoteIdentity_SkipsReingest()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("lookup-same-identity");
        var contentHash = FakeContentHash('a');
        var prefix = Shard.PrefixOf(contentHash);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(prefix);
        var originalEntry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(CreateShard(originalEntry), s_encryption), BlobTier.Cool);
        var initialIdentity = (await blobs.GetMetadataAsync(shardBlobName)).BlobIdentity;
        var firstSnapshot = RelativePath.Parse($"snapshots/{DateTimeOffset.UtcNow.AddMinutes(-1):yyyy-MM-ddTHHmmss.fffZ}");
        var secondSnapshot = RelativePath.Parse($"snapshots/{DateTimeOffset.UtcNow:yyyy-MM-ddTHHmmss.fffZ}");
        using (var firstIndex = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService([firstSnapshot]), repositoryKey, repositoryKey))
            (await firstIndex.LookupAsync(contentHash)).ShouldBe(originalEntry);

        var replacementEntry = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 6);
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(CreateShard(replacementEntry), s_encryption), BlobTier.Cool);
        blobs.SetBlobIdentity(shardBlobName, initialIdentity!);
        using var secondIndex = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService([firstSnapshot, secondSnapshot]), repositoryKey, repositoryKey);

        var result = await secondIndex.LookupAsync(contentHash);

        result.ShouldBe(originalEntry);
    }

    [Test]
    public async Task LookupAsync_BatchedHashes_LoadEachTouchedPrefixOnce()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("lookup-prefix-once");
        var firstHash = FakeContentHash('a');
        var secondHash = ContentHash.Parse($"{firstHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('b', 64 - ChunkIndexService.ShardPrefixLength)}");
        var otherHash = FakeContentHash('c');
        var firstEntry = new ShardEntry(firstHash, FakeChunkHash('1'), 10, 5);
        var secondEntry = new ShardEntry(secondHash, FakeChunkHash('2'), 20, 8);
        var otherEntry = new ShardEntry(otherHash, FakeChunkHash('3'), 30, 12);
        blobs.SeedBlob(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash)), await ShardSerializer.SerializeAsync(CreateShard(firstEntry, secondEntry), s_encryption), BlobTier.Cool);
        blobs.SeedBlob(BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherHash)), await ShardSerializer.SerializeAsync(CreateShard(otherEntry), s_encryption), BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var result = await index.LookupAsync([firstHash, secondHash, otherHash]);

        result[firstHash].ShouldBe(firstEntry);
        result[secondHash].ShouldBe(secondEntry);
        result[otherHash].ShouldBe(otherEntry);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash))).ShouldBe(1);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherHash))).ShouldBe(1);
    }

    [Test]
    public async Task LookupAsync_CorruptCleanSqlite_FailsWithLocalStoreRecoveryGuidance()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("lookup-sqlite-recovery");
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(CreateShard(entry), s_encryption), BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        (await index.LookupAsync(contentHash)).ShouldBe(entry);
        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        SqliteConnection.ClearAllPools();
        await cache.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None);

        var ex = await Should.ThrowAsync<ChunkIndexLocalStoreException>(() => index.LookupAsync(contentHash));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
        ex.Message.ShouldContain("repair command");
        cache.FileExists(RelativePath.Parse("cache.sqlite.bak")).ShouldBeFalse();
    }

    [Test]
    public async Task LookupAsync_UsesSqliteStateAndIgnoresLegacyPlaintextShardFiles()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("sqlite-only");
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(CreateShard(entry), s_encryption), BlobTier.Cool);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        cache.CreateDirectory(RelativePath.Root);
        await cache.WriteAllBytesAsync(RelativePath.Root / Shard.PrefixOf(contentHash), [1, 2, 3], CancellationToken.None);

        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var result = await index.LookupAsync(contentHash);

        result.ShouldBe(entry);
        blobs.RequestedBlobNames.ShouldContain(shardBlobName);
    }

    [Test]
    public void AddEntry_CorruptLocalStoreWithDirtyRows_FailsClearly()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("add-corrupt-dirty");
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);
        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5));
        SqliteConnection.ClearAllPools();
        cache.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None).GetAwaiter().GetResult();

        var ex = Should.Throw<ChunkIndexLocalStoreException>(() => index.AddEntry(new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6)));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
        ex.Message.ShouldContain("repair command");
    }

    [Test]
    public async Task LookupAsync_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var ex = await Should.ThrowAsync<ChunkIndexRepairIncompleteException>(() => index.LookupAsync(FakeContentHash('a')));

        ex.Message.ShouldContain("Rerun the explicit chunk-index repair command");
    }

    [Test]
    public void AddEntry_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker-add");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None).GetAwaiter().GetResult();
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        Should.Throw<ChunkIndexRepairIncompleteException>(() => index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5)));
    }

    [Test]
    public async Task FlushAsync_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker-flush");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        await Should.ThrowAsync<ChunkIndexRepairIncompleteException>(() => index.FlushAsync());
    }

    [Test]
    public async Task InvalidateCaches_DeletesShardCacheButKeepsRepairMarker()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("invalidate-marker");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        var cache = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        cache.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        await cache.WriteAllBytesAsync(RelativePath.Root / PathSegment.Parse("aa"), [1], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        index.InvalidateCaches();

        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeTrue();
        cache.FileExists(RelativePath.Root / PathSegment.Parse("aa")).ShouldBeFalse();
    }

    private static ChunkIndexService CreateIndex(FakeInMemoryBlobContainerService blobs, string name)
    {
        var repositoryKey = UniqueRepositoryKey(name);
        return new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
