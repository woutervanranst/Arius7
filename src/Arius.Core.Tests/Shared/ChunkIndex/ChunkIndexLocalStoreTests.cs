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
        version.ExecuteScalar().ShouldBe("2");

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

        store.UpdatePrefix(ChunkIndexRouter.GetLeafPrefix(contentHash), "remote-1", "snap-1", [cleanEntry]);

        store.FindEntry(contentHash).ShouldBe(dirtyEntry);
        store.FindDirtyEntry(contentHash).ShouldBe(dirtyEntry);
    }

    [Test]
    public void UpdatePrefix_WritesCleanRowAndValidationStateInSameStore()
    {
        var repositoryKey = $"acct-local-store-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var prefix = ChunkIndexRouter.GetLeafPrefix(entry.ContentHash);

        store.UpdatePrefix(prefix, "opaque-identity", "snapshot-1", [entry]);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindDirtyEntry(entry.ContentHash).ShouldBeNull();
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-1").ShouldBeTrue();
        store.CanReuseRemotePrefix(prefix, "opaque-identity").ShouldBeTrue();
    }

    [Test]
    public void ClearPrefix_PreservesDirtyRows_AndMarksRemoteMissing()
    {
        var repositoryKey = $"acct-local-store-reset-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var cleanEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var dirtyEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8);
        var prefix = ChunkIndexRouter.GetLeafPrefix(cleanEntry.ContentHash);
        store.UpdatePrefix(prefix, "remote-1", "snapshot-1", [cleanEntry]);
        store.UpsertDirty(dirtyEntry);

        store.ClearPrefix(prefix, "snapshot-2");

        store.FindEntry(cleanEntry.ContentHash).ShouldBeNull();
        store.FindDirtyEntry(cleanEntry.ContentHash).ShouldBeNull();
        store.FindEntry(dirtyEntry.ContentHash).ShouldBe(dirtyEntry);
        store.FindDirtyEntry(dirtyEntry.ContentHash).ShouldBe(dirtyEntry);
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-2").ShouldBeTrue();
        store.CanReuseRemotePrefix(prefix, "remote-1").ShouldBeFalse();
    }

    [Test]
    public void SetPrefixSnapshotVersion_UpdatesSnapshotVersionWithoutReingest()
    {
        var repositoryKey = $"acct-local-store-mark-validated-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var prefix = ChunkIndexRouter.GetLeafPrefix(entry.ContentHash);
        var originalSnapshotVersion = "snapshot-1";
        var latestSnapshotVersion = "snapshot-2";
        store.UpdatePrefix(prefix, "remote-1", originalSnapshotVersion, [entry]);

        store.SetPrefixSnapshotVersion(prefix, "remote-1", latestSnapshotVersion);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.IsPrefixAtSnapshotVersion(prefix, originalSnapshotVersion).ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(prefix, latestSnapshotVersion).ShouldBeTrue();
        store.CanReuseRemotePrefix(prefix, "remote-1").ShouldBeTrue();
    }

    [Test]
    public void MarkSynchronized_CleansDirtyRows_RemovesDirtyMarker_AndValidatesUploadedPrefixes()
    {
        var repositoryKey = $"acct-local-store-mark-synchronized-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var snapshotVersion = "snapshot-2";
        var firstEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        var secondEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8);
        var firstPrefix = ChunkIndexRouter.GetLeafPrefix(firstEntry.ContentHash);
        var secondPrefix = ChunkIndexRouter.GetLeafPrefix(secondEntry.ContentHash);
        store.UpsertDirtyRange([firstEntry, secondEntry]);

        store.HasDirtyMarker().ShouldBeTrue();

        store.MarkSynchronized([(firstPrefix, "remote-1"), (secondPrefix, "remote-2")], snapshotVersion);

        store.FindEntry(firstEntry.ContentHash).ShouldBe(firstEntry);
        store.FindEntry(secondEntry.ContentHash).ShouldBe(secondEntry);
        store.FindDirtyEntry(firstEntry.ContentHash).ShouldBeNull();
        store.FindDirtyEntry(secondEntry.ContentHash).ShouldBeNull();
        store.HasDirtyRows().ShouldBeFalse();
        store.HasDirtyMarker().ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(firstPrefix, snapshotVersion).ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(secondPrefix, snapshotVersion).ShouldBeTrue();
        store.CanReuseRemotePrefix(firstPrefix, "remote-1").ShouldBeTrue();
        store.CanReuseRemotePrefix(secondPrefix, "remote-2").ShouldBeTrue();
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
        store.UpdatePrefix(ChunkIndexRouter.GetLeafPrefix(clean.ContentHash), "remote-1", "snap-1", [clean]);

        store.ClearCleanCache();

        store.FindEntry(dirty.ContentHash).ShouldBe(dirty);
        store.FindDirtyEntry(dirty.ContentHash).ShouldBe(dirty);
        store.FindEntry(clean.ContentHash).ShouldBeNull();
        store.FindDirtyEntry(clean.ContentHash).ShouldBeNull();
        store.IsPrefixAtSnapshotVersion(ChunkIndexRouter.GetLeafPrefix(clean.ContentHash), "snap-1").ShouldBeFalse();
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
        version.ExecuteScalar().ShouldBe("2");
    }

    [Test]
    public void Initialize_UnsupportedSchemaVersion_RecreatesDisposableCache()
    {
        var repositoryKey = $"acct-local-store-schema-reset-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var fileSystem = new RelativeFileSystem(root);
        fileSystem.CreateDirectory(RelativePath.Root);

        using (var connection = OpenConnection(root))
        {
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE metadata (
                    key   TEXT NOT NULL PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT INTO metadata(key, value) VALUES ('schema_version', '1');

                CREATE TABLE loaded_prefixes (
                    prefix                      TEXT NOT NULL PRIMARY KEY,
                    remote_exists               INTEGER NOT NULL,
                    remote_blob_identity        TEXT,
                    validated_snapshot_identity TEXT NOT NULL
                );

                INSERT INTO loaded_prefixes(prefix, remote_exists, remote_blob_identity, validated_snapshot_identity)
                VALUES ('aa', 1, 'remote-1', 'snapshot-1');
                """;
            create.ExecuteNonQuery();
        }

        _ = new ChunkIndexLocalStore(root);

        using var upgradedConnection = OpenConnection(root);
        using var version = upgradedConnection.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("2");

        using var columnCheck = upgradedConnection.CreateCommand();
        columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('loaded_prefixes') WHERE name = 'snapshot_version';";
        columnCheck.ExecuteScalar().ShouldBe(1L);

        using var rowCount = upgradedConnection.CreateCommand();
        rowCount.CommandText = "SELECT COUNT(*) FROM loaded_prefixes;";
        rowCount.ExecuteScalar().ShouldBe(0L);
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
