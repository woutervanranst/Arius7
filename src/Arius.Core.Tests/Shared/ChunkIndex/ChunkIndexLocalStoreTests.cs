using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Microsoft.Data.Sqlite;
using Arius.Core.Shared.Storage;

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
    public void UpsertPendingFlush_AndLookup_RoundTripsEntry()
    {
        var repositoryKey = $"acct-local-store-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);

        store.UpsertPendingFlush(entry);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindPendingFlushEntry(entry.ContentHash).ShouldBe(entry);
        store.HasPendingFlushEntries().ShouldBeTrue();
        store.GetRootsWithPendingFlushes().ShouldBe([ChunkIndexRouter.GetRootPrefix(entry.ContentHash)]);

        using var connection = OpenConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(content_hash), length(chunk_hash), pending_flush FROM chunk_index_entries WHERE content_hash = $contentHash;";
        command.Parameters.Add("$contentHash", SqliteType.Blob).Value = Convert.FromHexString(entry.ContentHash.ToString());
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetInt64(0).ShouldBe(32);
        reader.GetInt64(1).ShouldBe(32);
        reader.GetInt64(2).ShouldBe(1);
    }

    [Test]
    [Arguments(BlobTier.Hot)]
    [Arguments(BlobTier.Cool)]
    [Arguments(BlobTier.Cold)]
    [Arguments(BlobTier.Archive)]
    public void UpsertAndFind_RoundTripsStorageTierHint(BlobTier tier)
    {
        var repositoryKey = $"acct-local-store-tier-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, tier);

        store.UpsertPendingFlush(entry);

        store.FindEntry(entry.ContentHash)!.StorageTierHint.ShouldBe(tier);
    }

    [Test]
    public void UpsertPendingFlush_DuplicateContentHash_LastWriterWins()
    {
        var repositoryKey = $"acct-local-store-duplicate-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var contentHash = FakeContentHash('a');
        var first = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var second = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 8, BlobTier.Cool);

        store.UpsertPendingFlush(first);
        store.UpsertPendingFlush(second);

        store.FindEntry(contentHash).ShouldBe(second);
        store.FindPendingFlushEntry(contentHash).ShouldBe(second);
    }

    [Test]
    public void UpdatePrefix_DoesNotOverwriteExistingPendingFlushRow()
    {
        var repositoryKey = $"acct-local-store-preserve-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var contentHash = FakeContentHash('a');
        var pendingFlushEntry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var remoteBackedEntry = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 8, BlobTier.Cool);
        store.UpsertPendingFlush(pendingFlushEntry);

        store.UpdatePrefix(ChunkIndexRouter.GetRootPrefix(contentHash), "remote-1", "snap-1", [remoteBackedEntry]);

        store.FindEntry(contentHash).ShouldBe(pendingFlushEntry);
        store.FindPendingFlushEntry(contentHash).ShouldBe(pendingFlushEntry);
    }

    [Test]
    public void UpdatePrefix_WritesRemoteBackedRowAndValidationStateInSameStore()
    {
        var repositoryKey = $"acct-local-store-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(entry.ContentHash);

        store.UpdatePrefix(prefix, "opaque-identity", "snapshot-1", [entry]);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindPendingFlushEntry(entry.ContentHash).ShouldBeNull();
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-1").ShouldBeTrue();
        store.IsPrefixAtETag(prefix, "opaque-identity").ShouldBeTrue();
    }

    [Test]
    public void AddEmptyPrefix_PreservesPendingFlushRows_AndMarksRemoteMissing()
    {
        var repositoryKey = $"acct-local-store-reset-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var remoteBackedEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var pendingFlushEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(remoteBackedEntry.ContentHash);
        store.UpdatePrefix(prefix, "remote-1", "snapshot-1", [remoteBackedEntry]);
        store.UpsertPendingFlush(pendingFlushEntry);

        store.AddEmptyPrefix(prefix, "snapshot-2");

        store.FindEntry(remoteBackedEntry.ContentHash).ShouldBeNull();
        store.FindPendingFlushEntry(remoteBackedEntry.ContentHash).ShouldBeNull();
        store.FindEntry(pendingFlushEntry.ContentHash).ShouldBe(pendingFlushEntry);
        store.FindPendingFlushEntry(pendingFlushEntry.ContentHash).ShouldBe(pendingFlushEntry);
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-2").ShouldBeTrue();
        store.IsPrefixAtETag(prefix, "remote-1").ShouldBeFalse();
    }

    [Test]
    public void SetPrefixSnapshotVersion_UpdatesSnapshotVersionWithoutReingest()
    {
        var repositoryKey = $"acct-local-store-mark-validated-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(entry.ContentHash);
        var originalSnapshotVersion = "snapshot-1";
        var latestSnapshotVersion = "snapshot-2";
        store.UpdatePrefix(prefix, "remote-1", originalSnapshotVersion, [entry]);

        store.SetPrefixSnapshotVersion(prefix, "remote-1", latestSnapshotVersion);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.IsPrefixAtSnapshotVersion(prefix, originalSnapshotVersion).ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(prefix, latestSnapshotVersion).ShouldBeTrue();
        store.IsPrefixAtETag(prefix, "remote-1").ShouldBeTrue();
    }

    [Test]
    public void PromoteToSnapshotVersion_UpdatesMatchingPrefixes_AndPreservesRemoteState()
    {
        var repositoryKey = $"acct-local-store-promote-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fromSnapshot = "snapshot-1";
        var toSnapshot = "snapshot-2";
        var remoteEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var remotePrefix = ChunkIndexRouter.GetRootPrefix(remoteEntry.ContentHash);
        var emptyPrefix = PathSegment.Parse("ff");
        var untouchedPrefix = PathSegment.Parse("ee");

        store.UpdatePrefix(remotePrefix, "remote-1", fromSnapshot, [remoteEntry]);
        store.AddEmptyPrefix(emptyPrefix, fromSnapshot);
        store.AddEmptyPrefix(untouchedPrefix, "snapshot-older");

        store.PromoteToSnapshotVersion(fromSnapshot, toSnapshot);

        store.FindEntry(remoteEntry.ContentHash).ShouldBe(remoteEntry);
        store.IsPrefixAtSnapshotVersion(remotePrefix, fromSnapshot).ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(emptyPrefix, fromSnapshot).ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(remotePrefix, toSnapshot).ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(emptyPrefix, toSnapshot).ShouldBeTrue();
        store.IsPrefixAtETag(remotePrefix, "remote-1").ShouldBeTrue();
        store.IsPrefixAtETag(emptyPrefix, "remote-1").ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(untouchedPrefix, "snapshot-older").ShouldBeTrue();
    }

    [Test]
    public void MarkPendingFlushesSynchronized_ClearsPendingFlushRows_AndValidatesUploadedPrefixes()
    {
        var repositoryKey = $"acct-local-store-mark-synchronized-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var snapshotVersion = "snapshot-2";
        var firstEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var secondEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8, BlobTier.Cool);
        var firstPrefix = ChunkIndexRouter.GetRootPrefix(firstEntry.ContentHash);
        var secondPrefix = ChunkIndexRouter.GetRootPrefix(secondEntry.ContentHash);
        store.UpsertPendingFlush(firstEntry);
        store.UpsertPendingFlush(secondEntry);

        store.MarkPendingFlushesSynchronized([(firstPrefix, "remote-1"), (secondPrefix, "remote-2")], snapshotVersion);

        store.FindEntry(firstEntry.ContentHash).ShouldBe(firstEntry);
        store.FindEntry(secondEntry.ContentHash).ShouldBe(secondEntry);
        store.FindPendingFlushEntry(firstEntry.ContentHash).ShouldBeNull();
        store.FindPendingFlushEntry(secondEntry.ContentHash).ShouldBeNull();
        store.HasPendingFlushEntries().ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(firstPrefix, snapshotVersion).ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(secondPrefix, snapshotVersion).ShouldBeTrue();
        store.IsPrefixAtETag(firstPrefix, "remote-1").ShouldBeTrue();
        store.IsPrefixAtETag(secondPrefix, "remote-2").ShouldBeTrue();
    }

    [Test]
    public void ClearRemoteBackedCache_PreservesPendingFlushRows_AndClearsLoadedPrefixes()
    {
        var repositoryKey = $"acct-local-store-clear-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var pendingFlush = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var remoteBacked = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6, BlobTier.Cool);
        store.UpsertPendingFlush(pendingFlush);
        store.UpdatePrefix(ChunkIndexRouter.GetRootPrefix(remoteBacked.ContentHash), "remote-1", "snap-1", [remoteBacked]);

        store.ClearRemoteBackedCache();

        store.FindEntry(pendingFlush.ContentHash).ShouldBe(pendingFlush);
        store.FindPendingFlushEntry(pendingFlush.ContentHash).ShouldBe(pendingFlush);
        store.FindEntry(remoteBacked.ContentHash).ShouldBeNull();
        store.FindPendingFlushEntry(remoteBacked.ContentHash).ShouldBeNull();
        store.IsPrefixAtSnapshotVersion(ChunkIndexRouter.GetRootPrefix(remoteBacked.ContentHash), "snap-1").ShouldBeFalse();
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

        store.UpsertPendingFlush(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));
        ClearPool(root);
        fileSystem.WriteAllBytesAsync(walPath, [1], CancellationToken.None).GetAwaiter().GetResult();
        fileSystem.WriteAllBytesAsync(shmPath, [2], CancellationToken.None).GetAwaiter().GetResult();

        store.RecreateDatabase(backupExisting: true);

        fileSystem.FileExists(RelativePath.Parse("cache.sqlite.bak")).ShouldBeTrue();
        fileSystem.FileExists(RelativePath.Parse("cache.sqlite-wal.bak")).ShouldBeTrue();
        fileSystem.FileExists(RelativePath.Parse("cache.sqlite-shm.bak")).ShouldBeTrue();
        fileSystem.FileExists(databasePath).ShouldBeTrue();
        store.HasPendingFlushEntries().ShouldBeFalse();

        using var connection = OpenConnection(root);
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("2");
    }

    [Test]
    public void FindEntry_CorruptRemoteBackedSqlite_FailsWithDisposableCacheGuidance()
    {
        var repositoryKey = $"acct-local-store-corrupt-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fileSystem = new RelativeFileSystem(root);

        ClearPool(root);
        fileSystem.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None).GetAwaiter().GetResult();

        var ex = Should.Throw<ChunkIndexLocalStoreException>(() => store.FindEntry(FakeContentHash('a')));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
    }

    [Test]
    public void UpsertPendingFlush_CorruptSqlite_FailsWithDisposableCacheGuidance()
    {
        var repositoryKey = $"acct-local-store-corrupt-dirty-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var fileSystem = new RelativeFileSystem(root);
        store.UpsertPendingFlush(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));

        ClearPool(root);
        fileSystem.WriteAllBytesAsync(RelativePath.Parse("cache.sqlite"), [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x64, 0x62], CancellationToken.None).GetAwaiter().GetResult();

        var ex = Should.Throw<ChunkIndexLocalStoreException>(() => store.UpsertPendingFlush(new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6, BlobTier.Cool)));

        ex.Message.ShouldContain("Delete the local chunk-index cache directory");
    }

    // ── Range queries (variable-depth prefixes) ──────────────────────────────

    [Test]
    public void ReadRangeEntries_OddNibblePrefix_FiltersInclusiveBounds()
    {
        var store = CreateStore("range-odd-nibble");
        var entries = new[] { Entry("aa2f"), Entry("aa30"), Entry("aa3f"), Entry("aa40") };
        store.UpsertPendingFlush(entries);

        var inRange = new List<ShardEntry>();
        store.ReadRangeEntries(PathSegment.Parse("aa3"), inRange.Add);

        inRange.Select(e => e.ContentHash).ShouldBe([entries[1].ContentHash, entries[2].ContentHash]);
        store.CountRangeEntries(PathSegment.Parse("aa3")).ShouldBe(2);
        store.CountRangeEntries(PathSegment.Parse("aa")).ShouldBe(4);
        store.CountRangeEntries(PathSegment.Parse("bb")).ShouldBe(0);
    }

    [Test]
    public void GetRootsWithPendingFlushes_AndGetPendingFlushHashes_FilterByRootRange()
    {
        var store = CreateStore("pending-roots");
        var pendingAa = Entry("aa1");
        var pendingBb = Entry("bb1");
        var cleanCc = Entry("cc1");
        store.UpsertPendingFlush(pendingAa);
        store.UpsertPendingFlush(pendingBb);
        store.UpdatePrefix(PathSegment.Parse("cc"), "remote-1", "snap-1", [cleanCc]);

        store.GetRootsWithPendingFlushes().ShouldBe([PathSegment.Parse("aa"), PathSegment.Parse("bb")]);
        store.GetPendingFlushHashes(PathSegment.Parse("aa")).ShouldBe([pendingAa.ContentHash]);
        store.GetStoredRootPrefixes().ShouldBe([PathSegment.Parse("aa"), PathSegment.Parse("bb"), PathSegment.Parse("cc")]);
    }

    // ── Coverage claims ──────────────────────────────────────────────────────

    [Test]
    public void FindCoveredPrefix_AncestorClaimCoversHash_AndRespectsSnapshotVersion()
    {
        var store = CreateStore("coverage-find");
        var entry = Entry("aa1");
        store.UpdatePrefix(PathSegment.Parse("aa"), "remote-1", "snap-1", [entry]);

        store.FindCoveredPrefix(ContentHash.Parse("aa3f".PadRight(64, '9')), "snap-1").ShouldBe(PathSegment.Parse("aa"));
        store.FindCoveredPrefix(ContentHash.Parse("aa3f".PadRight(64, '9')), "snap-2").ShouldBeNull(); // stale snapshot
        store.FindCoveredPrefix(ContentHash.Parse("bb00".PadRight(64, '9')), "snap-1").ShouldBeNull(); // other range
    }

    [Test]
    public void UpsertingCoverageClaim_RemovesOverlappingClaims_AndKeepsSiblings()
    {
        var store = CreateStore("coverage-overlap");
        store.AddEmptyPrefix(PathSegment.Parse("aa"), "snap-1");
        store.AddEmptyPrefix(PathSegment.Parse("ab"), "snap-1");

        // A deeper claim replaces its ancestor (split discovered), never its siblings.
        store.AddEmptyPrefix(PathSegment.Parse("aa3"), "snap-1");
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa"), "snap-1").ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa3"), "snap-1").ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("ab"), "snap-1").ShouldBeTrue();

        // A shallower claim replaces all its descendants (e.g. repair coarsened the layout).
        store.AddEmptyPrefix(PathSegment.Parse("aa"), "snap-2");
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa3"), "snap-1").ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa"), "snap-2").ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("ab"), "snap-1").ShouldBeTrue();
    }

    // ── Schema versioning ────────────────────────────────────────────────────

    [Test]
    public void Initialize_SchemaVersionMismatch_RecreatesDatabase()
    {
        var repositoryKey = $"acct-local-store-schema-mismatch-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        store.UpsertPendingFlush(Entry("aa1"));

        SqliteConnection.ClearAllPools();
        using (var connection = OpenConnection(root))
        using (var downgrade = connection.CreateCommand())
        {
            downgrade.CommandText = "UPDATE metadata SET value = '1' WHERE key = 'schema_version';";
            downgrade.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new ChunkIndexLocalStore(root);

        reopened.HasPendingFlushEntries().ShouldBeFalse(); // recreated, not migrated
        using var verify = OpenConnection(root);
        using var version = verify.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("2");
    }

    private static ChunkIndexLocalStore CreateStore(string name)
    {
        var repositoryKey = $"acct-local-store-{name}-{Guid.NewGuid():N}";
        return new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
    }

    /// <summary>An entry whose content hash starts with <paramref name="hashPrefix"/> (padded with '9').</summary>
    private static ShardEntry Entry(string hashPrefix)
        => new(ContentHash.Parse(hashPrefix.PadRight(64, '9')), FakeChunkHash('e'), 10, 5, BlobTier.Cool);

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

    private static void ClearPool(LocalDirectory root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(RelativePath.Parse("cache.sqlite")),
            Pooling    = true,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        SqliteConnection.ClearPool(connection);
    }
}
