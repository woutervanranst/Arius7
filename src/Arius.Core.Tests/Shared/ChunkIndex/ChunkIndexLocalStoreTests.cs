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

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindDirtyEntry(entry.ContentHash).ShouldBe(entry);
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

        store.FindEntry(contentHash).ShouldBe(second);
        store.FindDirtyEntry(contentHash).ShouldBe(second);
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

        store.FindEntry(contentHash).ShouldBe(dirtyEntry);
        store.FindDirtyEntry(contentHash).ShouldBe(dirtyEntry);
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

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindDirtyEntry(entry.ContentHash).ShouldBeNull();
        store.GetLoadedPrefixState(prefix).ShouldBe(new LoadedPrefixState(prefix, true, "opaque-identity", "snapshot-1"));
    }

    [Test]
    public void ClearPrefix_PreservesDirtyRows_AndUpdatesLoadedPrefixState()
    {
        var repositoryKey = $"acct-local-store-reset-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var cleanEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var dirtyEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8);
        var prefix = ChunkIndexRouter.GetLeafPrefix(cleanEntry.ContentHash);
        store.IngestCleanPrefix(new LoadedPrefixState(prefix, true, "remote-1", "snapshot-1"), [cleanEntry]);
        store.UpsertDirty(dirtyEntry);

        store.ClearPrefix(new LoadedPrefixState(prefix, false, null, "snapshot-2"));

        store.FindEntry(cleanEntry.ContentHash).ShouldBeNull();
        store.FindDirtyEntry(cleanEntry.ContentHash).ShouldBeNull();
        store.FindEntry(dirtyEntry.ContentHash).ShouldBe(dirtyEntry);
        store.FindDirtyEntry(dirtyEntry.ContentHash).ShouldBe(dirtyEntry);
        store.GetLoadedPrefixState(prefix).ShouldBe(new LoadedPrefixState(prefix, false, null, "snapshot-2"));
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

        store.FindEntry(dirty.ContentHash).ShouldBe(dirty);
        store.FindDirtyEntry(dirty.ContentHash).ShouldBe(dirty);
        store.FindEntry(clean.ContentHash).ShouldBeNull();
        store.FindDirtyEntry(clean.ContentHash).ShouldBeNull();
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

    [Test]
    public void FindEntry_CorruptCleanSqlite_FailsWithDisposableCacheGuidance()
    {
        var repositoryKey = $"acct-local-store-corrupt-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fileSystem = new RelativeFileSystem(root);

        SqliteConnection.ClearAllPools();
        fileSystem.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None).GetAwaiter().GetResult();

        var ex = Should.Throw<ChunkIndexLocalStoreException>(() => store.FindEntry(FakeContentHash('a')));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
    }

    [Test]
    public void UpsertDirty_CorruptSqliteWithDirtyMarker_FailsWithUnflushedEntryGuidance()
    {
        var repositoryKey = $"acct-local-store-corrupt-dirty-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fileSystem = new RelativeFileSystem(root);
        store.UpsertDirty(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5));

        SqliteConnection.ClearAllPools();
        fileSystem.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None).GetAwaiter().GetResult();

        var ex = Should.Throw<ChunkIndexLocalStoreException>(() => store.UpsertDirty(new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6)));

        ex.Message.ShouldContain("unflushed entries may exist");
        ex.Message.ShouldContain("Rerun archive");
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
