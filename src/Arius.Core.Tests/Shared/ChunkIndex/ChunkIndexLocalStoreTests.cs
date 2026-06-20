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
    public void UpsertPendingFlush_AndLookup_RoundTripsEntry()
    {
        var repositoryKey = $"acct-local-store-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);

        store.UpsertPendingFlush(entry);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindPendingFlushEntry(entry.ContentHash).ShouldBe(entry);
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
    public void IngestCoverage_DownloadedPrefix_DoesNotOverwriteExistingPendingFlushRow()
    {
        var repositoryKey = $"acct-local-store-preserve-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var contentHash = FakeContentHash('a');
        var pendingFlushEntry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var remoteBackedEntry = new ShardEntry(contentHash, FakeChunkHash('c'), 20, 8, BlobTier.Cool);
        store.UpsertPendingFlush(pendingFlushEntry);

        store.IngestCoverage("snap-1", [(ChunkIndexRouter.GetRootPrefix(contentHash), "remote-1", new[] { remoteBackedEntry })], [], []);

        store.FindEntry(contentHash).ShouldBe(pendingFlushEntry);
        store.FindPendingFlushEntry(contentHash).ShouldBe(pendingFlushEntry);
    }

    [Test]
    public void IngestCoverage_DownloadedPrefix_WritesRemoteBackedRowAndValidationStateInSameStore()
    {
        var repositoryKey = $"acct-local-store-clean-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(entry.ContentHash);

        store.IngestCoverage("snapshot-1", [(prefix, "opaque-identity", new[] { entry })], [], []);

        store.FindEntry(entry.ContentHash).ShouldBe(entry);
        store.FindPendingFlushEntry(entry.ContentHash).ShouldBeNull();
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-1").ShouldBeTrue();
        store.IsPrefixAtETag(prefix, "opaque-identity").ShouldBeTrue();
    }

    [Test]
    public void IngestCoverage_EmptyPrefix_PreservesPendingFlushRows_AndMarksRemoteMissing()
    {
        var repositoryKey = $"acct-local-store-reset-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var remoteBackedEntry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var pendingFlushEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 8, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(remoteBackedEntry.ContentHash);
        SeedRemotePrefix(store, prefix, "remote-1", "snapshot-1", remoteBackedEntry);
        store.UpsertPendingFlush(pendingFlushEntry);

        store.IngestCoverage("snapshot-2", [], [], [prefix]);

        store.FindEntry(remoteBackedEntry.ContentHash).ShouldBeNull();
        store.FindPendingFlushEntry(remoteBackedEntry.ContentHash).ShouldBeNull();
        store.FindEntry(pendingFlushEntry.ContentHash).ShouldBe(pendingFlushEntry);
        store.FindPendingFlushEntry(pendingFlushEntry.ContentHash).ShouldBe(pendingFlushEntry);
        store.IsPrefixAtSnapshotVersion(prefix, "snapshot-2").ShouldBeTrue();
        store.IsPrefixAtETag(prefix, "remote-1").ShouldBeFalse();
    }

    [Test]
    public void IngestCoverage_RevalidatedPrefix_UpdatesSnapshotVersionWithoutReingest()
    {
        var repositoryKey = $"acct-local-store-mark-validated-{Guid.NewGuid():N}";
        var root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var store = new ChunkIndexLocalStore(root);
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var prefix = ChunkIndexRouter.GetRootPrefix(entry.ContentHash);
        var originalSnapshotVersion = "snapshot-1";
        var latestSnapshotVersion = "snapshot-2";
        SeedRemotePrefix(store, prefix, "remote-1", originalSnapshotVersion, entry);

        store.IngestCoverage(latestSnapshotVersion, [], [(prefix, "remote-1")], []);

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

        SeedRemotePrefix(store, remotePrefix, "remote-1", fromSnapshot, remoteEntry);
        SeedEmptyPrefix(store, emptyPrefix, fromSnapshot);
        SeedEmptyPrefix(store, untouchedPrefix, "snapshot-older");

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
        store.GetRootsWithPendingFlushes().ShouldBeEmpty();
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
        SeedRemotePrefix(store, ChunkIndexRouter.GetRootPrefix(remoteBacked.ContentHash), "remote-1", "snap-1", remoteBacked);

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
        store.GetRootsWithPendingFlushes().ShouldBeEmpty();

        using var connection = OpenConnection(root);
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        version.ExecuteScalar().ShouldBe("1");
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
    }

    [Test]
    public void CountRangeEntries_CountsOnlyHashesInPrefixRange()
    {
        var store = CreateStore("range-count");
        store.UpsertPendingFlush(new[] { Entry("aa2f"), Entry("aa30"), Entry("aa3f"), Entry("aa40") });

        store.CountRangeEntries(PathSegment.Parse("aa3")).ShouldBe(2); // aa30, aa3f
        store.CountRangeEntries(PathSegment.Parse("aa")).ShouldBe(4);
        store.CountRangeEntries(PathSegment.Parse("bb")).ShouldBe(0);
    }

    [Test]
    public void EnrichThinChunks_FillsThinTierAndSizeFromParentTar_AndLeavesLargeChunksUntouched()
    {
        var store     = CreateStore("enrich-thin");
        var thin      = ContentHash.Parse("aa".PadRight(64, '1'));
        var parentTar = ChunkHash.Parse("bb".PadRight(64, '2'));
        var large     = ContentHash.Parse("cc".PadRight(64, '3'));

        // Repair stages a thin row with its parent in chunk_hash and placeholder tier/size; a large chunk's
        // chunk_hash equals its content_hash and already carries its own final tier/size.
        store.UpsertRemoteBacked(new[]
        {
            new ShardEntry(thin, parentTar, 10, 0, BlobTier.Cool),
            new ShardEntry(large, ChunkHash.Parse(large), 100, 3, BlobTier.Cool),
        });

        store.EnrichThinChunks(new Dictionary<ChunkHash, (BlobTier Tier, long ChunkSize)> { [parentTar] = (BlobTier.Archive, 2) });

        store.FindEntry(thin).ShouldBe(new ShardEntry(thin, parentTar, 10, 2, BlobTier.Archive));
        store.FindEntry(large).ShouldBe(new ShardEntry(large, ChunkHash.Parse(large), 100, 3, BlobTier.Cool));
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
        SeedRemotePrefix(store, PathSegment.Parse("cc"), "remote-1", "snap-1", cleanCc);

        store.GetRootsWithPendingFlushes().ShouldBe([PathSegment.Parse("aa"), PathSegment.Parse("bb")]);
        store.GetPendingFlushHashes(PathSegment.Parse("aa")).ShouldBe([pendingAa.ContentHash]);
        store.GetStoredRootPrefixes().ShouldBe([PathSegment.Parse("aa"), PathSegment.Parse("bb"), PathSegment.Parse("cc")]);
    }

    // ── Coverage claims ──────────────────────────────────────────────────────

    [Test]
    public void FindCoveredPrefixes_AncestorClaimCoversHash_AndRespectsSnapshotVersion()
    {
        var store = CreateStore("coverage-find");
        var entry = Entry("aa1");
        SeedRemotePrefix(store, PathSegment.Parse("aa"), "remote-1", "snap-1", entry);

        var covered = ContentHash.Parse("aa3f".PadRight(64, '9'));
        var stale = ContentHash.Parse("aa3f".PadRight(64, '9'));
        var otherRange = ContentHash.Parse("bb00".PadRight(64, '9'));

        store.FindCoveredPrefixes([covered], "snap-1")
            .ShouldBe(new Dictionary<ContentHash, PathSegment> { [covered] = PathSegment.Parse("aa") });
        store.FindCoveredPrefixes([stale], "snap-2").ShouldBeEmpty();
        store.FindCoveredPrefixes([otherRange], "snap-1").ShouldBeEmpty();
    }

    [Test]
    public void FindCoveredPrefixes_ReturnsMatchingClaimsForOneRoot_AndRespectsSnapshotVersion()
    {
        var store = CreateStore("coverage-find-batch");
        SeedEmptyPrefix(store, PathSegment.Parse("aa3"), "snap-1");

        var covered = ContentHash.Parse("aa3f".PadRight(64, '9'));
        var uncoveredSibling = ContentHash.Parse("aa4f".PadRight(64, '9'));
        var otherRoot = ContentHash.Parse("bb00".PadRight(64, '9'));

        store.FindCoveredPrefixes([covered, uncoveredSibling, otherRoot], "snap-1")
            .ShouldBe(new Dictionary<ContentHash, PathSegment>
            {
                [covered] = PathSegment.Parse("aa3"),
            });

        store.FindCoveredPrefixes([covered], "snap-2").ShouldBeEmpty();
    }

    [Test]
    public void FindCoveredPrefixes_DeepClaim_StillResolvedWithinProbeDepthCap()
    {
        var store = CreateStore("coverage-deep");
        // A claim several levels deep (well within the candidate probe cap) still covers its own hashes only.
        SeedEmptyPrefix(store, PathSegment.Parse("aa3f0"), "snap-1");

        var covered = ContentHash.Parse("aa3f0".PadRight(64, '9'));
        var sibling = ContentHash.Parse("aa3f1".PadRight(64, '9'));

        store.FindCoveredPrefixes([covered, sibling], "snap-1")
            .ShouldBe(new Dictionary<ContentHash, PathSegment> { [covered] = PathSegment.Parse("aa3f0") });
    }

    [Test]
    public void UpsertingCoverageClaim_RemovesOverlappingClaims_AndKeepsSiblings()
    {
        var store = CreateStore("coverage-overlap");
        SeedEmptyPrefix(store, PathSegment.Parse("aa"), "snap-1");
        SeedEmptyPrefix(store, PathSegment.Parse("ab"), "snap-1");

        // A deeper claim replaces its ancestor (split discovered), never its siblings.
        SeedEmptyPrefix(store, PathSegment.Parse("aa3"), "snap-1");
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa"), "snap-1").ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa3"), "snap-1").ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("ab"), "snap-1").ShouldBeTrue();

        // A shallower claim replaces all its descendants (e.g. repair coarsened the layout).
        SeedEmptyPrefix(store, PathSegment.Parse("aa"), "snap-2");
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa3"), "snap-1").ShouldBeFalse();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("aa"), "snap-2").ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(PathSegment.Parse("ab"), "snap-1").ShouldBeTrue();
    }

    private static ChunkIndexLocalStore CreateStore(string name)
    {
        var repositoryKey = $"acct-local-store-{name}-{Guid.NewGuid():N}";
        return new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
    }

    /// <summary>Seeds a remote-backed prefix through the production write path (<see cref="ChunkIndexLocalStore.IngestCoverage"/>).</summary>
    private static void SeedRemotePrefix(ChunkIndexLocalStore store, PathSegment prefix, string etag, string snapshot, params ShardEntry[] entries)
        => store.IngestCoverage(snapshot, [(prefix, etag, entries)], [], []);

    /// <summary>Seeds an empty (remote-missing) prefix claim through the production write path.</summary>
    private static void SeedEmptyPrefix(ChunkIndexLocalStore store, PathSegment prefix, string snapshot)
        => store.IngestCoverage(snapshot, [], [], [prefix]);

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
