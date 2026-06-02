using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceLookupTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task LookupAsync_MissingRemoteShard_ReturnsMiss()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        using var index = CreateIndex(blobs, "missing");

        var actual = await index.LookupAsync(FakeContentHash('a'));

        actual.ShouldBeNull();
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.ChunksPrefix);
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
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shard = CreateShard(entry);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(shard, s_encryption), BlobTier.Cool);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        cache.CreateDirectory(RelativePath.Root);
        await cache.WriteAllBytesAsync(RelativePath.Root / Shard.PrefixOf(contentHash), [1, 2, 3], CancellationToken.None);

        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBe(entry);
    }

    [Test]
    public async Task LookupAsync_RemoteShard_DownloadsWithoutMetadataCheck()
    {
        var blobs = new ThrowOnMetadataBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("remote-no-metadata");
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shard = CreateShard(entry);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash)),
            await ShardSerializer.SerializeAsync(shard, s_encryption),
            BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBe(entry);
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
    public async Task LookupAsync_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

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
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

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
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

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
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        index.InvalidateCaches();

        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeTrue();
        cache.FileExists(RelativePath.Root / PathSegment.Parse("aa")).ShouldBeFalse();
    }

    private static ChunkIndexService CreateIndex(FakeInMemoryBlobContainerService blobs, string name)
    {
        var repositoryKey = UniqueRepositoryKey(name);
        return new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";

    private sealed class ThrowOnMetadataBlobContainerService : IBlobContainerService
    {
        private readonly FakeInMemoryBlobContainerService _inner = new();

        public void SeedBlob(RelativePath blobName, byte[] content, BlobTier? tier = null) =>
            _inner.SeedBlob(blobName, content, tier);

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) =>
            _inner.CreateContainerIfNotExistsAsync(cancellationToken);

        public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
            _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

        public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
            _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

        public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
            _inner.DownloadAsync(blobName, cancellationToken);

        public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
            _inner.TryDownloadAsync(blobName, cancellationToken);

        public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Chunk-index shard lookup must not perform metadata checks.");

        public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
            _inner.ListAsync(prefix, includeMetadata, cancellationToken);

        public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
            _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

        public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
            _inner.SetTierAsync(blobName, tier, cancellationToken);

        public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
            _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

        public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(blobName, cancellationToken);
    }
}
