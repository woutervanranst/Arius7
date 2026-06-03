using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexLocalStoreTests
{
    [Test]
    public void Initialize_CreatesSchemaVersionAndWalMode()
    {
        var repositoryKey = $"acct-local-store-init-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        _ = new ChunkIndexLocalStore(root);

        using var connection = OpenConnection(root);
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("1");

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode;";
        journalMode.ExecuteScalar().ShouldBe("wal");
    }

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

        using var connection = OpenConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(content_hash), length(chunk_hash), dirty FROM chunk_index_entries WHERE content_hash = $contentHash;";
        command.Parameters.Add("$contentHash", SqliteType.Blob).Value = Convert.FromHexString(entry.ContentHash.ToString());
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetInt64(0).ShouldBe(32);
        reader.GetInt64(1).ShouldBe(32);
        reader.GetInt64(2).ShouldBe(1);
    }

    [Test]
    public void UpsertDirtyRange_DuplicateContentHash_LastWriterWins()
    {
        var repositoryKey = $"acct-local-store-duplicate-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var contentHash = FakeContentHash('a');
        var first = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var second = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 8);

        store.UpsertDirtyRange([first, second]);

        store.GetValueOrDefault(contentHash).ShouldBe(second);
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
    public void IngestCleanPrefix_WritesCleanRowAndLoadedPrefixStateInSameStore()
    {
        var repositoryKey = $"acct-local-store-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var prefix = ChunkIndexRouter.GetLeafPrefix(entry.ContentHash);

        store.IngestCleanPrefix(new LoadedPrefixState(prefix, true, "opaque-identity", "snapshot-1"), [entry]);

        var row = store.GetRowOrDefault(entry.ContentHash);
        row.ShouldNotBeNull();
        row!.IsDirty.ShouldBeFalse();
        store.GetLoadedPrefixState(prefix).ShouldBe(new LoadedPrefixState(prefix, true, "opaque-identity", "snapshot-1"));
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

    [Test]
    public void MarkDirtyPrefixesClean_RemovesDirtyMarkerOnlyWhenNoDirtyRowsRemain()
    {
        var repositoryKey = $"acct-local-store-marker-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var first = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var second = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6);
        store.UpsertDirtyRange([first, second]);

        store.HasDirtyMarker().ShouldBeTrue();

        store.MarkDirtyPrefixesClean([ChunkIndexRouter.GetLeafPrefix(first.ContentHash)]);

        store.HasDirtyMarker().ShouldBeTrue();

        store.MarkDirtyPrefixesClean([ChunkIndexRouter.GetLeafPrefix(second.ContentHash)]);

        store.HasDirtyRows().ShouldBeFalse();
        store.HasDirtyMarker().ShouldBeFalse();
    }

    [Test]
    public void RecreateDatabase_MovesAsideExistingSqliteFamily_AndReinitializesSchema()
    {
        var repositoryKey = $"acct-local-store-recreate-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fileSystem = new RelativeFileSystem(root);
        var databasePath = RelativePath.Parse("cache.sqlite");
        var walPath = RelativePath.Parse("cache.sqlite-wal");
        var shmPath = RelativePath.Parse("cache.sqlite-shm");

        store.UpsertDirty(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5));
        SqliteConnection.ClearAllPools();
        fileSystem.WriteAllBytesAsync(walPath, [1], CancellationToken.None).GetAwaiter().GetResult();
        fileSystem.WriteAllBytesAsync(shmPath, [2], CancellationToken.None).GetAwaiter().GetResult();

        store.RecreateDatabase(backupExisting: true);

        fileSystem.FileExists(RelativePath.Parse("cache.sqlite.bak")).ShouldBeTrue();
        fileSystem.FileExists(RelativePath.Parse("cache.sqlite-wal.bak")).ShouldBeTrue();
        fileSystem.FileExists(RelativePath.Parse("cache.sqlite-shm.bak")).ShouldBeTrue();
        fileSystem.FileExists(databasePath).ShouldBeTrue();
        store.HasDirtyRows().ShouldBeFalse();

        using var connection = OpenConnection(root);
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("1");
    }

    private static SqliteConnection OpenConnection(LocalDirectory root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(RelativePath.Parse("cache.sqlite")),
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }
}
