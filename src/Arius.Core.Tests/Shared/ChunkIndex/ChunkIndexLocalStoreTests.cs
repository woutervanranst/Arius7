using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexLocalStoreTests
{
    [Test]
    public void UpsertDirty_AndLookup_RoundTripsEntry()
    {
        var repositoryKey = $"acct-local-store-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);

        store.UpsertDirty(entry);

        store.GetValueOrDefault(entry.ContentHash).ShouldBe(entry);
        store.HasDirtyRows().ShouldBeTrue();
        store.GetDirtyPrefixes().ShouldBe([ChunkIndexRouter.GetLeafPrefix(entry.ContentHash)]);
    }

    [Test]
    public void IngestCleanPrefix_DoesNotOverwriteExistingDirtyRow()
    {
        var repositoryKey = $"acct-local-store-preserve-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var contentHash = FakeContentHash('a');
        var dirtyEntry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var cleanEntry = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 8);
        store.UpsertDirty(dirtyEntry);

        store.IngestCleanPrefix(
            new LoadedPrefixState(ChunkIndexRouter.GetLeafPrefix(contentHash), true, "remote-1", "snap-1"),
            [cleanEntry]);

        store.GetValueOrDefault(contentHash).ShouldBe(dirtyEntry);
    }

    [Test]
    public void ReadPrefixEntries_ReturnsEntriesOrderedByContentHash()
    {
        var repositoryKey = $"acct-local-store-order-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var first = new ShardEntry(ContentHash.Parse($"aa{new string('1', 62)}"), FakeChunkHash('b'), 10, 5);
        var second = new ShardEntry(ContentHash.Parse($"aa{new string('2', 62)}"), FakeChunkHash('c'), 11, 6);
        store.UpsertDirtyRange([second, first]);

        var entries = new List<ShardEntry>();
        store.ReadPrefixEntries(PathSegment.Parse("aa"), entries.Add);

        entries.ShouldBe([first, second]);
    }

    [Test]
    public void ClearCleanCache_PreservesDirtyRows_AndClearsLoadedPrefixes()
    {
        var repositoryKey = $"acct-local-store-clear-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var dirty = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var clean = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6);
        store.UpsertDirty(dirty);
        store.IngestCleanPrefix(new LoadedPrefixState(ChunkIndexRouter.GetLeafPrefix(clean.ContentHash), true, "remote-1", "snap-1"), [clean]);

        store.ClearCleanCache();

        store.GetValueOrDefault(dirty.ContentHash).ShouldBe(dirty);
        store.GetValueOrDefault(clean.ContentHash).ShouldBeNull();
        store.GetLoadedPrefixState(ChunkIndexRouter.GetLeafPrefix(clean.ContentHash)).ShouldBeNull();
    }
}
