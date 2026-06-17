using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// SQLite-backed local chunk-index store for remote-backed shard entries, prefix validation state, and pending local flush entries.
/// </summary>
internal sealed class ChunkIndexLocalStore
{
    private const string SchemaVersion = "1";

    // Deepest coverage-claim prefix FindCoveredPrefixes probes for. A claim at depth d implies ~16^(d-2)
    // shards per root; depth 8 is ~4 trillion shards — far beyond any real repository. A (never-produced)
    // deeper claim is simply not matched here and re-validates harmlessly, so this is correctness-safe.
    private const int MaxCoveragePrefixLength = 8;

    private readonly RelativeFileSystem            _fileSystem;
    private readonly ILogger<ChunkIndexLocalStore> _logger;
    private readonly LocalDirectory                _rootDirectory;
    private readonly RelativePath                  _databasePath = RelativePath.Root / PathSegment.Parse("cache.sqlite");
    private readonly string                        _connectionString;
    private readonly Lock                          _localStateGate = new();

    // -- LIFECYCLE ------------------------------------------------------------

    /// <summary>
    /// Opens the local store rooted at <paramref name="root"/> and ensures the SQLite schema exists.
    /// </summary>
    public ChunkIndexLocalStore(LocalDirectory root, ILogger<ChunkIndexLocalStore>? logger = null)
    {
        _rootDirectory = root;
        _logger        = logger ?? NullLogger<ChunkIndexLocalStore>.Instance;

        _fileSystem = new RelativeFileSystem(root);
        _fileSystem.CreateDirectory(RelativePath.Root);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(_databasePath),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString();

        Initialize();
    }

    /// <summary>
    /// Applies SQLite pragmas and creates or upgrades the local-store schema.
    /// </summary>
    private void Initialize()
    {
        try
        {
            CreateOrUpgradeSchema();
            _logger.LogDebug("[chunk-index-local] Initialize: database={DatabasePath}", _rootDirectory.Resolve(_databasePath));
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    // -- FIND ---------------------------------------------------------------

    /// <summary>
    /// Returns the locally stored entry for <paramref name="contentHash"/> regardless of whether that entry is remote-backed or pending flush.
    /// Use this only when the caller has already handled remote-validation requirements for remote-backed cached data,
    /// or when the caller intentionally accepts either state.
    /// </summary>
    public ShardEntry? FindEntry(ContentHash contentHash) => FindEntryCore(contentHash, pendingFlushOnly: false);

    /// <summary>
    /// Returns the locally stored entry for <paramref name="contentHash"/> only when that entry is still pending local flush.
    /// Use this before remote validation when pending flush entries must win immediately over remote shard state.
    /// Returns <c>null</c> for missing entries and for remote-backed cached entries.
    /// </summary>
    public ShardEntry? FindPendingFlushEntry(ContentHash contentHash) => FindEntryCore(contentHash, pendingFlushOnly: true);

    private ShardEntry? FindEntryCore(ContentHash contentHash, bool pendingFlushOnly)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = pendingFlushOnly
                ? "SELECT content_hash, chunk_hash, original_size, chunk_size, storage_tier_hint FROM chunk_index_entries WHERE content_hash = $contentHash AND pending_flush = 1;"
                : "SELECT content_hash, chunk_hash, original_size, chunk_size, storage_tier_hint FROM chunk_index_entries WHERE content_hash = $contentHash;";
            command.Parameters.Add("$contentHash", SqliteType.Blob).Value = ParseHashBytes(contentHash.ToString());
            using var reader = command.ExecuteReader();
            var entry = reader.Read() ? ReadEntry(reader) : null;
            _logger.LogDebug("[chunk-index-local] FindEntry: contentHash={ContentHash} pendingFlushOnly={PendingFlushOnly} found={Found}", contentHash.Short8, pendingFlushOnly, entry is not null);
            return entry;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns the validated coverage claim covering each of the specified hashes at the specified snapshot
    /// version. A hash is covered by at most one (non-overlapping) claim, whose prefix is a prefix of the hash,
    /// so only those candidate prefixes — each hash's prefix path — are queried, never every claim under the root.
    /// </summary>
    public IReadOnlyDictionary<ContentHash, PathSegment> FindCoveredPrefixes(IReadOnlyList<ContentHash> contentHashes, string snapshotVersion)
    {
        if (contentHashes.Count == 0)
            return new Dictionary<ContentHash, PathSegment>();

        try
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contentHash in contentHashes)
            {
                var hashHex = contentHash.ToString();
                var maxLength = Math.Min(hashHex.Length, MaxCoveragePrefixLength);
                for (var length = ChunkIndexService.MinShardPrefixLength; length <= maxLength; length++)
                    candidates.Add(hashHex[..length]);
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT prefix FROM loaded_prefixes
                WHERE snapshot_version = $snapshotVersion
                  AND prefix IN (SELECT value FROM json_each($candidates));
                """;
            command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion);
            command.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(candidates));

            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    prefixes.Add(reader.GetString(0));
            }

            var covered = new Dictionary<ContentHash, PathSegment>();
            if (prefixes.Count > 0)
            {
                foreach (var contentHash in contentHashes)
                {
                    var hashHex = contentHash.ToString();
                    var maxLength = Math.Min(hashHex.Length, MaxCoveragePrefixLength);
                    for (var length = ChunkIndexService.MinShardPrefixLength; length <= maxLength; length++)
                    {
                        var prefix = hashHex[..length];
                        if (!prefixes.Contains(prefix))
                            continue;

                        covered[contentHash] = PathSegment.Parse(prefix);
                        break;
                    }
                }
            }

            _logger.LogDebug("[chunk-index-local] FindCoveredPrefixes: hashes={HashCount} candidates={CandidateCount} snapshotVersion={SnapshotVersion} covered={CoveredCount}", contentHashes.Count, candidates.Count, snapshotVersion, covered.Count);
            return covered;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns whether the prefix has already been validated for the specified snapshot version.
    /// </summary>
    public bool IsPrefixAtSnapshotVersion(PathSegment prefix, string snapshotVersion)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM loaded_prefixes WHERE prefix = $prefix AND snapshot_version = $snapshotVersion LIMIT 1);";
            command.Parameters.AddWithValue("$prefix", prefix.ToString());
            command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion);
            var isValidated = command.ExecuteScalar() is long value && value != 0;
            _logger.LogDebug("[chunk-index-local] IsPrefixAtSnapshotVersion: prefix={Prefix} snapshotVersion={SnapshotVersion} value={IsValidated}", prefix, snapshotVersion, isValidated);
            return isValidated;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns whether the prefix already has a locally cached remote-backed copy for the specified remote shard identity.
    /// </summary>
    public bool IsPrefixAtETag(PathSegment prefix, string etag)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM loaded_prefixes WHERE prefix = $prefix AND remote_exists = 1 AND remote_blob_identity = $etag LIMIT 1);";
            command.Parameters.AddWithValue("$prefix", prefix.ToString());
            command.Parameters.AddWithValue("$etag", etag);
            var isAtETag = command.ExecuteScalar() is long value && value != 0;
            _logger.LogDebug("[chunk-index-local] IsPrefixAtETag: prefix={Prefix} etag={Etag} value={IsAtETag}", prefix, etag, isAtETag);
            return isAtETag;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns prefixes that contain entries still pending local flush to remote shard blobs.
    /// </summary>
    public IReadOnlyList<PathSegment> GetRootsWithPendingFlushes()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT lower(substr(hex(content_hash), 1, {ChunkIndexService.MinShardPrefixLength})) FROM chunk_index_entries WHERE pending_flush = 1 ORDER BY 1;";
            using var reader = command.ExecuteReader();
            var prefixes = new List<PathSegment>();
            while (reader.Read())
                prefixes.Add(PathSegment.Parse(reader.GetString(0)));

            _logger.LogDebug("[chunk-index-local] GetRootsWithPendingFlushes: count={Count}", prefixes.Count);
            return prefixes;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns the content hashes of entries pending local flush within range of <paramref name="prefix"/>.
    /// </summary>
    public IReadOnlyList<ContentHash> GetPendingFlushHashes(PathSegment prefix)
    {
        try
        {
            var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(prefix);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_hash FROM chunk_index_entries WHERE pending_flush = 1 AND content_hash BETWEEN $lower AND $upper ORDER BY content_hash;";
            command.Parameters.Add("$lower", SqliteType.Blob).Value = lower;
            command.Parameters.Add("$upper", SqliteType.Blob).Value = upper;
            using var reader = command.ExecuteReader();
            var hashes = new List<ContentHash>();
            while (reader.Read())
                hashes.Add(ContentHash.FromDigest((byte[])reader.GetValue(0)));

            _logger.LogDebug("[chunk-index-local] GetPendingFlushHashes: prefix={Prefix} count={Count}", prefix, hashes.Count);
            return hashes;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns prefixes represented by any local chunk-index entry.
    /// </summary>
    public IEnumerable<PathSegment> GetStoredRootPrefixes()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT lower(substr(hex(content_hash), 1, {ChunkIndexService.MinShardPrefixLength})) FROM chunk_index_entries ORDER BY 1;";
            using var reader = command.ExecuteReader();
            var prefixes = new List<PathSegment>();
            while (reader.Read())
                prefixes.Add(PathSegment.Parse(reader.GetString(0)));

            _logger.LogDebug("[chunk-index-local] GetStoredRootPrefixes: count={Count}", prefixes.Count);
            return prefixes;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Streams all locally stored entries for <paramref name="prefix"/>, including remote-backed and pending-flush rows.
    /// </summary>
    public void ReadRangeEntries(PathSegment prefix, Action<ShardEntry> consume)
    {
        try
        {
            var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(prefix);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_hash, chunk_hash, original_size, chunk_size, storage_tier_hint FROM chunk_index_entries WHERE content_hash BETWEEN $lower AND $upper ORDER BY content_hash;";
            command.Parameters.Add("$lower", SqliteType.Blob).Value = lower;
            command.Parameters.Add("$upper", SqliteType.Blob).Value = upper;
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                consume(ReadEntry(reader));
                count++;
            }

            _logger.LogDebug("[chunk-index-local] ReadRangeEntries: prefix={Prefix} count={Count}", prefix, count);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns the number of entries currently stored within range of <paramref name="prefix"/>.
    /// </summary>
    public int CountRangeEntries(PathSegment prefix)
    {
        try
        {
            var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(prefix);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM chunk_index_entries WHERE content_hash BETWEEN $lower AND $upper;";
            command.Parameters.Add("$lower", SqliteType.Blob).Value = lower;
            command.Parameters.Add("$upper", SqliteType.Blob).Value = upper;
            var count = Convert.ToInt32(command.ExecuteScalar());
            _logger.LogDebug("[chunk-index-local] CountRangeEntries: prefix={Prefix} count={Count}", prefix, count);
            return count;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns whether the local store currently contains entries pending local flush.
    /// </summary>
    public bool HasPendingFlushEntries()
    {
        try
        {
            using var connection = OpenConnection();
            var hasPendingFlushEntries = HasPendingFlushEntries(connection);
            _logger.LogDebug("[chunk-index-local] HasPendingFlushEntries: value={HasPendingFlushEntries}", hasPendingFlushEntries);
            return hasPendingFlushEntries;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    // -- PENDING FLUSH WRITES -------------------------------------------------

    /// <summary>
    /// Records a newly discovered entry as pending local flush until it is uploaded to remote shard blobs.
    /// </summary>
    public void UpsertPendingFlush(ShardEntry entry)
    {
        try
        {
            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = CreateUpsertCommand(connection, transaction, pendingFlush: true, preservePendingFlushRows: false);
                BindEntry(command, entry);
                var rowsAffected = command.ExecuteNonQuery();

                transaction.Commit();
                _logger.LogDebug("[chunk-index-local] UpsertPendingFlush: contentHash={ContentHash} rowsAffected={RowsAffected}", entry.ContentHash.Short8, rowsAffected);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Records multiple entries as pending local flush in a single transaction. Equivalent to calling
    /// <see cref="UpsertPendingFlush(Arius.Core.Shared.ChunkIndex.ShardEntry)"/> for each entry, but commits once to amortize the per-entry transaction cost.
    /// </summary>
    public void UpsertPendingFlush(IEnumerable<ShardEntry> entries)
    {
        var materialized = entries.ToArray();
        if (materialized.Length == 0)
            return;

        try
        {
            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = CreateUpsertCommand(connection, transaction, pendingFlush: true, preservePendingFlushRows: false);
                var rowsAffected = 0;
                foreach (var entry in materialized)
                {
                    BindEntry(command, entry);
                    rowsAffected += command.ExecuteNonQuery();
                }

                transaction.Commit();
                _logger.LogDebug("[chunk-index-local] UpsertPendingFlushBatch: entries={Entries} rowsAffected={RowsAffected}", materialized.Length, rowsAffected);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    // -- REMOTE-BACKED CACHE --------------------------------------------------

    /// <summary>
    /// Records a known remote-backed entry, typically during explicit repair from authoritative chunk blobs.
    /// </summary>
    public void UpsertRemoteBacked(ShardEntry entry)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = CreateUpsertCommand(connection, transaction, pendingFlush: false, preservePendingFlushRows: false);
            BindEntry(command, entry);
            var rowsAffected = command.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogDebug("[chunk-index-local] UpsertRemoteBacked: contentHash={ContentHash} rowsAffected={RowsAffected}", entry.ContentHash.Short8, rowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Applies one coverage-resolution pass for a batch of prefixes in a single transaction: downloaded shards
    /// (replace remote-backed range + record etag), revalidated shards (advance snapshot version only), and
    /// empty ranges (clear remote-backed range + mark missing). The prefixes are pairwise non-nested — they come
    /// from <see cref="ChunkIndexRouter.ResolveTarget"/>'s parent-wins walk, which returns the shallowest existing
    /// shard per hash — so their coverage-overlap deletes cannot clobber one another. Batching keeps the parallel
    /// shard-download fan-out from contending on the SQLite write lock (one transaction, one writer).
    /// </summary>
    public void IngestCoverage(
        string                                                                          snapshotVersion,
        IReadOnlyCollection<(PathSegment Prefix, string Etag, IReadOnlyList<ShardEntry> Entries)> downloaded,
        IReadOnlyCollection<(PathSegment Prefix, string Etag)>                           revalidated,
        IReadOnlyCollection<PathSegment>                                                 emptyPrefixes)
    {
        if (downloaded.Count == 0 && revalidated.Count == 0 && emptyPrefixes.Count == 0)
            return;

        try
        {
            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var upsertEntries = CreateUpsertCommand(connection, transaction, pendingFlush: false, preservePendingFlushRows: true);

                foreach (var (prefix, etag, entries) in downloaded)
                {
                    DeleteRemoteBackedRange(connection, transaction, prefix);
                    foreach (var entry in entries)
                    {
                        BindEntry(upsertEntries, entry);
                        upsertEntries.ExecuteNonQuery();
                    }

                    UpsertLoadedPrefix(connection, transaction, prefix, remoteExists: true, etag, snapshotVersion);
                }

                foreach (var (prefix, etag) in revalidated)
                    UpsertLoadedPrefix(connection, transaction, prefix, remoteExists: true, etag, snapshotVersion);

                foreach (var prefix in emptyPrefixes)
                {
                    DeleteRemoteBackedRange(connection, transaction, prefix);
                    UpsertLoadedPrefix(connection, transaction, prefix, remoteExists: false, ETag: null, snapshotVersion);
                }

                transaction.Commit();
                _logger.LogDebug("[chunk-index-local] IngestCoverage: downloaded={Downloaded} revalidated={Revalidated} empty={Empty}", downloaded.Count, revalidated.Count, emptyPrefixes.Count);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Promotes loaded-prefix validation from one snapshot version to another while preserving recorded remote state.
    /// </summary>
    public void PromoteToSnapshotVersion(string oldSnapshotVersion, string newSnapshotVersion)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE loaded_prefixes SET snapshot_version = $newSnapshotVersion WHERE snapshot_version = $oldSnapshotVersion;";
            command.Parameters.AddWithValue("$oldSnapshotVersion", oldSnapshotVersion);
            command.Parameters.AddWithValue("$newSnapshotVersion", newSnapshotVersion);
            var rowsAffected = command.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogDebug("[chunk-index-local] PromoteToSnapshotVersion: from={OldSnapshotVersion} to={NewSnapshotVersion} rowsAffected={RowsAffected}", oldSnapshotVersion, newSnapshotVersion, rowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Marks uploaded pending entries as synchronized by clearing pending-flush flags and validating uploaded prefixes for the specified snapshot.
    /// </summary>
    public void MarkPendingFlushesSynchronized(IEnumerable<(PathSegment Prefix, string Etag)> states, string snapshotVersion)
    {
        try
        {
            var materialized = states.ToArray();
            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE chunk_index_entries SET pending_flush = 0 WHERE pending_flush = 1;";
                var synchronizedRowsAffected = command.ExecuteNonQuery();

                var validatedRowsAffected = 0;
                foreach (var state in materialized)
                    validatedRowsAffected += UpsertLoadedPrefix(connection, transaction, state.Prefix, remoteExists: true, state.Etag, snapshotVersion);

                transaction.Commit();

                _logger.LogDebug("[chunk-index-local] MarkPendingFlushesSynchronized: prefixes={Prefixes} synchronizedRowsAffected={SynchronizedRowsAffected} validatedRowsAffected={ValidatedRowsAffected}", materialized.Length, synchronizedRowsAffected, validatedRowsAffected);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Clears all disposable remote-backed cache state while preserving pending flush rows.
    /// </summary>
    public void ClearRemoteBackedCache()
    {
        try
        {
            // Under the same gate as the other remote-backed writers (IngestCoverage / pending-flush), so a
            // cleared cache and a coverage ingest can never interleave into a half-cleared state.
            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var deleteEntries = connection.CreateCommand();
                deleteEntries.Transaction = transaction;
                deleteEntries.CommandText = "DELETE FROM chunk_index_entries WHERE pending_flush = 0;";
                var deletedEntryRows = deleteEntries.ExecuteNonQuery();

                using var deletePrefixes = connection.CreateCommand();
                deletePrefixes.Transaction = transaction;
                deletePrefixes.CommandText = "DELETE FROM loaded_prefixes;";
                var deletedPrefixRows = deletePrefixes.ExecuteNonQuery();
                transaction.Commit();

                DeleteLegacyShardCacheFiles();
                _logger.LogDebug("[chunk-index-local] ClearRemoteBackedCache: deletedEntryRows={DeletedEntryRows} deletedPrefixRows={DeletedPrefixRows}", deletedEntryRows, deletedPrefixRows);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    // -- RECOVERY -------------------------------------------------------------

    /// <summary>
    /// Recreates the SQLite database after local-store corruption, optionally keeping backup files for inspection.
    /// </summary>
    public void RecreateDatabase(bool backupExisting)
    {
        try
        {
            lock (_localStateGate)
            {
                ClearConnectionPool();
                var replacedFiles = 0;
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var path = backupExisting
                        ? RelativePath.Parse($"cache.sqlite{suffix}.bak")
                        : default;
                    var current = RelativePath.Parse($"cache.sqlite{suffix}");
                    if (!_fileSystem.FileExists(current))
                        continue;

                    replacedFiles++;

                    if (backupExisting)
                    {
                        if (_fileSystem.FileExists(path))
                            _fileSystem.DeleteFile(path);
                        File.Move(_rootDirectory.Resolve(current), _rootDirectory.Resolve(path), overwrite: true);
                    }
                    else
                    {
                        _fileSystem.DeleteFile(current);
                    }
                }

                CreateOrUpgradeSchema();
                _logger.LogDebug("[chunk-index-local] RecreateDatabase: backupExisting={BackupExisting} replacedFiles={ReplacedFiles}", backupExisting, replacedFiles);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    private ChunkIndexLocalStoreException CreateLocalStoreException(SqliteException ex)
    {
        var path = _rootDirectory.Resolve(_databasePath);
        var message = $"Local chunk-index cache '{path}' is unreadable. Delete the local chunk-index cache directory '{_rootDirectory}' and retry, or run the explicit chunk-index repair command if the problem persists.";

        return new ChunkIndexLocalStoreException(message, ex);
    }

    // -- SQLITE HELPERS -------------------------------------------------------

    private void CreateOrUpgradeSchema()
    {
        using var connection = OpenConnection();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = wal; PRAGMA synchronous = normal;";
        pragma.ExecuteNonQuery();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                CHECK (length(key) > 0),
                CHECK (length(value) > 0)
            );

            CREATE TABLE IF NOT EXISTS chunk_index_entries (
                content_hash    BLOB NOT NULL PRIMARY KEY,
                chunk_hash      BLOB NOT NULL,
                original_size   INTEGER NOT NULL CHECK (original_size >= 0),
                chunk_size INTEGER NOT NULL CHECK (chunk_size >= 0),
                storage_tier_hint INTEGER NOT NULL CHECK (storage_tier_hint BETWEEN 1 AND 4),
                pending_flush   INTEGER NOT NULL DEFAULT 0 CHECK (pending_flush IN (0, 1)),
                CHECK (length(content_hash) = 32),
                CHECK (length(chunk_hash) = 32)
            );

            CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_pending_flush
                ON chunk_index_entries(pending_flush) WHERE pending_flush = 1;

            CREATE TABLE IF NOT EXISTS loaded_prefixes (
                prefix                      TEXT NOT NULL PRIMARY KEY,
                remote_exists               INTEGER NOT NULL CHECK (remote_exists IN (0, 1)),
                remote_blob_identity        TEXT,
                snapshot_version            TEXT NOT NULL,
                CHECK (length(prefix) > 0),
                CHECK (length(snapshot_version) > 0)
            );

            CREATE INDEX IF NOT EXISTS ix_loaded_prefixes_snapshot_prefix
                ON loaded_prefixes(snapshot_version, prefix);
            """;
        create.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "INSERT INTO metadata(key, value) VALUES ('schema_version', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        version.Parameters.AddWithValue("$value", SchemaVersion);
        version.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void ClearConnectionPool()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static bool HasPendingFlushEntries(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chunk_index_entries WHERE pending_flush = 1 LIMIT 1);";
        return command.ExecuteScalar() is long count && count != 0;
    }

    private static int DeleteRemoteBackedRange(SqliteConnection connection, SqliteTransaction transaction, PathSegment prefix)
    {
        var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(prefix);
        using var deleteEntries = connection.CreateCommand();
        deleteEntries.Transaction = transaction;
        deleteEntries.CommandText = "DELETE FROM chunk_index_entries WHERE pending_flush = 0 AND content_hash BETWEEN $lower AND $upper;";
        deleteEntries.Parameters.Add("$lower", SqliteType.Blob).Value = lower;
        deleteEntries.Parameters.Add("$upper", SqliteType.Blob).Value = upper;
        return deleteEntries.ExecuteNonQuery();
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction, bool pendingFlush, bool preservePendingFlushRows)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = preservePendingFlushRows
            ? """
                INSERT INTO chunk_index_entries(content_hash, chunk_hash, original_size, chunk_size, storage_tier_hint, pending_flush)
                VALUES ($contentHash, $chunkHash, $originalSize, $chunkSize, $storageTierHint, $pendingFlush)
                ON CONFLICT(content_hash) DO UPDATE SET
                    chunk_hash = excluded.chunk_hash,
                    original_size = excluded.original_size,
                    chunk_size = excluded.chunk_size,
                    storage_tier_hint = excluded.storage_tier_hint,
                    pending_flush = excluded.pending_flush
                WHERE chunk_index_entries.pending_flush = 0;
                """
            : """
                INSERT INTO chunk_index_entries(content_hash, chunk_hash, original_size, chunk_size, storage_tier_hint, pending_flush)
                VALUES ($contentHash, $chunkHash, $originalSize, $chunkSize, $storageTierHint, $pendingFlush)
                ON CONFLICT(content_hash) DO UPDATE SET
                    chunk_hash = excluded.chunk_hash,
                    original_size = excluded.original_size,
                    chunk_size = excluded.chunk_size,
                    storage_tier_hint = excluded.storage_tier_hint,
                    pending_flush = excluded.pending_flush;
                """;

        command.Parameters.Add("$contentHash", SqliteType.Blob);
        command.Parameters.Add("$chunkHash", SqliteType.Blob);
        command.Parameters.Add("$originalSize", SqliteType.Integer);
        command.Parameters.Add("$chunkSize", SqliteType.Integer);
        command.Parameters.Add("$storageTierHint", SqliteType.Integer);
        command.Parameters.Add("$pendingFlush", SqliteType.Integer).Value = pendingFlush ? 1 : 0;
        return command;
    }

    private static void BindEntry(SqliteCommand command, ShardEntry entry)
    {
        command.Parameters["$contentHash"].Value = ParseHashBytes(entry.ContentHash.ToString());
        command.Parameters["$chunkHash"].Value = ParseHashBytes(entry.ChunkHash.ToString());
        command.Parameters["$originalSize"].Value = entry.OriginalSize;
        command.Parameters["$chunkSize"].Value = entry.ChunkSize;
        command.Parameters["$storageTierHint"].Value = ShardEntry.SerializeTier(entry.StorageTierHint);
    }

    private static int UpsertLoadedPrefix(SqliteConnection connection, SqliteTransaction transaction, PathSegment prefix, bool remoteExists, string? ETag, string snapshotVersion)
    {
        // Coverage claims must never overlap: replacing a range claim removes any claim that is a
        // strict ancestor or strict descendant of the inserted prefix (never siblings).
        using var deleteOverlapping = connection.CreateCommand();
        deleteOverlapping.Transaction = transaction;
        deleteOverlapping.CommandText = """
            DELETE FROM loaded_prefixes
            WHERE prefix <> $prefix
              AND (substr($prefix, 1, length(prefix)) = prefix
                OR substr(prefix, 1, length($prefix)) = $prefix);
            """;
        deleteOverlapping.Parameters.AddWithValue("$prefix", prefix.ToString());
        deleteOverlapping.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO loaded_prefixes(prefix, remote_exists, remote_blob_identity, snapshot_version)
            VALUES ($prefix, $remoteExists, $ETag, $snapshotVersion)
            ON CONFLICT(prefix) DO UPDATE SET
                remote_exists = excluded.remote_exists,
                remote_blob_identity = excluded.remote_blob_identity,
                snapshot_version = excluded.snapshot_version;
            """;
        command.Parameters.AddWithValue("$prefix", prefix.ToString());
        command.Parameters.Add("$remoteExists", SqliteType.Integer).Value = remoteExists ? 1 : 0;
        command.Parameters.Add("$ETag", SqliteType.Text).Value = (object?)ETag ?? DBNull.Value;
        command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion);
        return command.ExecuteNonQuery();
    }

    private static ShardEntry ReadEntry(SqliteDataReader reader)
        => new(
            ContentHash.FromDigest((byte[])reader.GetValue(0)),
            ChunkHash.FromDigest((byte[])reader.GetValue(1)),
            reader.GetInt64(2),
            reader.GetInt64(3),
            ShardEntry.DeserializeTier(reader.GetInt32(4)));

    private static byte[] ParseHashBytes(string value)
        => Convert.FromHexString(value);

    private void DeleteLegacyShardCacheFiles()
    {
        foreach (var fileName in _fileSystem.EnumerateFileNames(RelativePath.Root))
        {
            if (IsLegacyShardCacheFile(fileName))
                _fileSystem.DeleteFile(RelativePath.Root / fileName);
        }
    }

    private static bool IsLegacyShardCacheFile(PathSegment fileName)
    {
        var value = fileName.ToString();
        return value.Length == 2 && value.All(Uri.IsHexDigit);
    }
}
