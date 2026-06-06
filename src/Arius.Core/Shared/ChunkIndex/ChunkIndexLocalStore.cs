using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// SQLite-backed local chunk-index cache for clean remote shard data and unflushed dirty entries.
/// </summary>
internal sealed class ChunkIndexLocalStore : IDisposable
{
    private const string SchemaVersion = "1";

    private readonly RelativeFileSystem _fileSystem;
    private readonly ILogger<ChunkIndexLocalStore> _logger;
    private readonly LocalDirectory     _rootDirectory;
    private readonly RelativePath       _databasePath    = RelativePath.Root / PathSegment.Parse("cache.sqlite");
    private readonly RelativePath       _dirtyMarkerPath = RelativePath.Root / PathSegment.Parse("dirty.marker");
    private readonly string             _connectionString;
    private readonly Lock               _localStateGate = new();

    // -- LIFECYCLE ------------------------------------------------------------

    /// <summary>
    /// Opens the local store rooted at <paramref name="root"/> and ensures the SQLite schema exists.
    /// </summary>
    public ChunkIndexLocalStore(LocalDirectory root, ILogger<ChunkIndexLocalStore>? logger = null)
    {
        _rootDirectory = root;
        _fileSystem    = new RelativeFileSystem(root);
        _logger        = logger ?? NullLogger<ChunkIndexLocalStore>.Instance;
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
    public void Initialize()
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

    /// <summary>
    /// Releases resources held by the store.
    /// </summary>
    public void Dispose()
    {
    }

    // -- LOOKUP ---------------------------------------------------------------

    /// <summary>
    /// Returns the locally stored entry for <paramref name="contentHash"/> regardless of whether that entry is clean or dirty.
    /// Use this only when the caller has already handled remote-validation requirements for clean cached data,
    /// or when the caller intentionally accepts either state.
    /// </summary>
    public ShardEntry? FindEntry(ContentHash contentHash) => FindEntryCore(contentHash, dirtyOnly: false);

    /// <summary>
    /// Returns the locally stored entry for <paramref name="contentHash"/> only when that entry is still dirty.
    /// Use this before remote validation when dirty entries must win immediately over remote shard state.
    /// Returns <c>null</c> for missing entries and for clean cached entries.
    /// </summary>
    public ShardEntry? FindDirtyEntry(ContentHash contentHash) => FindEntryCore(contentHash, dirtyOnly: true);

    private ShardEntry? FindEntryCore(ContentHash contentHash, bool dirtyOnly)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = dirtyOnly
                ? "SELECT content_hash, chunk_hash, original_size, compressed_size FROM chunk_index_entries WHERE content_hash = $contentHash AND dirty = 1;"
                : "SELECT content_hash, chunk_hash, original_size, compressed_size FROM chunk_index_entries WHERE content_hash = $contentHash;";
            command.Parameters.Add("$contentHash", SqliteType.Blob).Value = ParseHashBytes(contentHash.ToString());
            using var reader = command.ExecuteReader();
            var entry = reader.Read() ? ReadEntry(reader) : null;
            _logger.LogDebug("[chunk-index-local] FindEntry: contentHash={ContentHash} dirtyOnly={DirtyOnly} found={Found}", contentHash.Short8, dirtyOnly, entry is not null);
            return entry;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns the cached remote-validation state for <paramref name="prefix"/>, if it is known locally.
    /// </summary>
    public LoadedPrefixState? GetLoadedPrefixState(PathSegment prefix)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT remote_exists, remote_blob_identity, validated_snapshot_identity FROM loaded_prefixes WHERE prefix = $prefix;";
            command.Parameters.AddWithValue("$prefix", prefix.ToString());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                _logger.LogDebug("[chunk-index-local] GetLoadedPrefixState: prefix={Prefix} found=false", prefix);
                return null;
            }

            var state = new LoadedPrefixState(
                prefix,
                reader.GetInt64(0) != 0,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2));
            _logger.LogDebug("[chunk-index-local] GetLoadedPrefixState: prefix={Prefix} found=true remoteExists={RemoteExists}", prefix, state.RemoteExists);
            return state;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns prefixes that contain dirty entries which still need to be flushed to remote shard blobs.
    /// </summary>
    public IReadOnlyList<PathSegment> GetDirtyPrefixes()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT prefix FROM chunk_index_entries WHERE dirty = 1 ORDER BY prefix;";
            using var reader = command.ExecuteReader();
            var prefixes = new List<PathSegment>();
            while (reader.Read())
                prefixes.Add(PathSegment.Parse(reader.GetString(0)));

            _logger.LogDebug("[chunk-index-local] GetDirtyPrefixes: count={Count}", prefixes.Count);
            return prefixes;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns prefixes represented by any local chunk-index row.
    /// </summary>
    public IReadOnlyList<PathSegment> GetStoredPrefixes()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT prefix FROM chunk_index_entries ORDER BY prefix;";
            using var reader = command.ExecuteReader();
            var prefixes = new List<PathSegment>();
            while (reader.Read())
                prefixes.Add(PathSegment.Parse(reader.GetString(0)));

            _logger.LogDebug("[chunk-index-local] GetStoredPrefixes: count={Count}", prefixes.Count);
            return prefixes;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Streams all entries for <paramref name="prefix"/> in deterministic content-hash order.
    /// </summary>
    public void ReadPrefixEntries(PathSegment prefix, Action<ShardEntry> consume)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_hash, chunk_hash, original_size, compressed_size FROM chunk_index_entries WHERE prefix = $prefix ORDER BY content_hash;";
            command.Parameters.AddWithValue("$prefix", prefix.ToString());
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                consume(ReadEntry(reader));
                count++;
            }

            _logger.LogDebug("[chunk-index-local] ReadPrefixEntries: prefix={Prefix} count={Count}", prefix, count);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns whether the local store currently contains unflushed dirty entries.
    /// </summary>
    public bool HasDirtyRows()
    {
        try
        {
            using var connection = OpenConnection();
            var hasDirtyRows = HasDirtyRows(connection);
            _logger.LogDebug("[chunk-index-local] HasDirtyRows: value={HasDirtyRows}", hasDirtyRows);
            return hasDirtyRows;
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Returns whether the crash-safety marker indicates that dirty rows may exist.
    /// </summary>
    public bool HasDirtyMarker()
    {
        var hasDirtyMarker = _fileSystem.FileExists(_dirtyMarkerPath);
        _logger.LogDebug("[chunk-index-local] HasDirtyMarker: value={HasDirtyMarker}", hasDirtyMarker);
        return hasDirtyMarker;
    }

    // -- DIRTY WRITES ---------------------------------------------------------

    /// <summary>
    /// Records a newly discovered entry as dirty until it is flushed to the remote chunk index.
    /// </summary>
    public void UpsertDirty(ShardEntry entry) => UpsertDirtyRange([entry]);

    /// <summary>
    /// Records newly discovered entries as dirty until they are flushed to the remote chunk index.
    /// </summary>
    public void UpsertDirtyRange(IEnumerable<ShardEntry> entries)
    {
        try
        {
            var materialized = entries.ToArray();
            if (materialized.Length == 0)
                return;

            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = CreateUpsertCommand(connection, transaction, dirty: true, preserveDirtyRows: false);
                var rowsAffected = 0;
                foreach (var entry in materialized)
                {
                    BindEntry(command, entry);
                    rowsAffected += command.ExecuteNonQuery();
                }

                transaction.Commit();
                var dirtyMarkerWritten = WriteDirtyMarker();
                _logger.LogDebug("[chunk-index-local] UpsertDirtyRange: entries={Entries} rowsAffected={RowsAffected} dirtyMarkerWritten={DirtyMarkerWritten}", materialized.Length, rowsAffected, dirtyMarkerWritten);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Marks dirty rows in the provided prefixes as clean after their shards have been uploaded.
    /// </summary>
    public void MarkDirtyPrefixesClean(IReadOnlyCollection<PathSegment> prefixes)
    {
        try
        {
            if (prefixes.Count == 0)
                return;

            lock (_localStateGate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE chunk_index_entries SET dirty = 0 WHERE prefix = $prefix AND dirty = 1;";
                var prefixParameter = command.Parameters.Add("$prefix", SqliteType.Text);
                var rowsAffected = 0;
                foreach (var prefix in prefixes)
                {
                    prefixParameter.Value = prefix.ToString();
                    rowsAffected += command.ExecuteNonQuery();
                }

                transaction.Commit();

                var hasDirtyRows = HasDirtyRows(connection);
                var dirtyMarkerDeleted = !hasDirtyRows && DeleteDirtyMarker();
                _logger.LogDebug("[chunk-index-local] MarkDirtyPrefixesClean: prefixes={Prefixes} rowsAffected={RowsAffected} hasDirtyRows={HasDirtyRows} dirtyMarkerDeleted={DirtyMarkerDeleted}", prefixes.Count, rowsAffected, hasDirtyRows, dirtyMarkerDeleted);
            }
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    // -- CLEAN CACHE ----------------------------------------------------------

    /// <summary>
    /// Records a known-clean entry, typically during explicit repair from remote chunk blobs.
    /// </summary>
    public void UpsertClean(ShardEntry entry)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = CreateUpsertCommand(connection, transaction, dirty: false, preserveDirtyRows: false);
            BindEntry(command, entry);
            var rowsAffected = command.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogDebug("[chunk-index-local] UpsertClean: contentHash={ContentHash} rowsAffected={RowsAffected}", entry.ContentHash.Short8, rowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Replaces local clean rows for a loaded prefix while preserving any dirty rows for the same content hashes.
    /// </summary>
    public void IngestCleanPrefix(LoadedPrefixState loadedPrefix, IEnumerable<ShardEntry> entries)
    {
        try
        {
            var materialized = entries.ToArray();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = CreateUpsertCommand(connection, transaction, dirty: false, preserveDirtyRows: true);
            var entryRowsAffected = 0;
            foreach (var entry in materialized)
            {
                BindEntry(command, entry);
                entryRowsAffected += command.ExecuteNonQuery();
            }

            var loadedPrefixRowsAffected = UpsertLoadedPrefix(connection, transaction, loadedPrefix);
            transaction.Commit();
            _logger.LogDebug("[chunk-index-local] IngestCleanPrefix: prefix={Prefix} entries={Entries} entryRowsAffected={EntryRowsAffected} loadedPrefixRowsAffected={LoadedPrefixRowsAffected}", loadedPrefix.Prefix, materialized.Length, entryRowsAffected, loadedPrefixRowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Deletes clean cached rows and validation state for <paramref name="prefix"/> while preserving dirty rows.
    /// </summary>
    public void DeleteCleanPrefix(PathSegment prefix)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chunk_index_entries WHERE prefix = $prefix AND dirty = 0; DELETE FROM loaded_prefixes WHERE prefix = $prefix;";
            command.Parameters.AddWithValue("$prefix", prefix.ToString());
            var rowsAffected = command.ExecuteNonQuery();
            _logger.LogDebug("[chunk-index-local] DeleteCleanPrefix: prefix={Prefix} rowsAffected={RowsAffected}", prefix, rowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Updates the cached remote-validation state for a prefix.
    /// </summary>
    public void UpdateLoadedPrefixState(LoadedPrefixState loadedPrefix)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var rowsAffected = UpsertLoadedPrefix(connection, transaction, loadedPrefix);
            transaction.Commit();
            _logger.LogDebug("[chunk-index-local] UpdateLoadedPrefixState: prefix={Prefix} rowsAffected={RowsAffected}", loadedPrefix.Prefix, rowsAffected);
        }
        catch (SqliteException ex)
        {
            throw CreateLocalStoreException(ex);
        }
    }

    /// <summary>
    /// Clears all disposable clean cache state while preserving unflushed dirty rows.
    /// </summary>
    public void ClearCleanCache()
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var deleteEntries = connection.CreateCommand();
            deleteEntries.Transaction = transaction;
            deleteEntries.CommandText = "DELETE FROM chunk_index_entries WHERE dirty = 0;";
            var deletedEntryRows = deleteEntries.ExecuteNonQuery();

            using var deletePrefixes = connection.CreateCommand();
            deletePrefixes.Transaction = transaction;
            deletePrefixes.CommandText = "DELETE FROM loaded_prefixes;";
            var deletedPrefixRows = deletePrefixes.ExecuteNonQuery();
            transaction.Commit();

            DeleteLegacyShardCacheFiles();
            _logger.LogDebug("[chunk-index-local] ClearCleanCache: deletedEntryRows={DeletedEntryRows} deletedPrefixRows={DeletedPrefixRows}", deletedEntryRows, deletedPrefixRows);
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
                SqliteConnection.ClearAllPools();
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

                var dirtyMarkerDeleted = DeleteDirtyMarker();
                CreateOrUpgradeSchema();
                _logger.LogDebug("[chunk-index-local] RecreateDatabase: backupExisting={BackupExisting} replacedFiles={ReplacedFiles} dirtyMarkerDeleted={DirtyMarkerDeleted}", backupExisting, replacedFiles, dirtyMarkerDeleted);
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
        var message = HasDirtyMarker()
            ? $"Local chunk-index cache '{path}' is unreadable while unflushed entries may exist. Rerun archive to finish flushing the chunk index, or run the explicit chunk-index repair command if archive cannot recover."
            : $"Local chunk-index cache '{path}' is unreadable. Delete the local chunk-index cache directory '{_rootDirectory}' and retry, or run the explicit chunk-index repair command if the problem persists.";

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
                compressed_size INTEGER NOT NULL CHECK (compressed_size >= 0),
                prefix          TEXT NOT NULL,
                dirty           INTEGER NOT NULL DEFAULT 0 CHECK (dirty IN (0, 1)),
                recorded_order  INTEGER,
                CHECK (length(content_hash) = 32),
                CHECK (length(chunk_hash) = 32),
                CHECK (length(prefix) > 0)
            );

            CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_prefix
                ON chunk_index_entries(prefix, content_hash);

            CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_dirty_prefix
                ON chunk_index_entries(dirty, prefix);

            CREATE TABLE IF NOT EXISTS loaded_prefixes (
                prefix                      TEXT NOT NULL PRIMARY KEY,
                remote_exists               INTEGER NOT NULL CHECK (remote_exists IN (0, 1)),
                remote_blob_identity        TEXT,
                validated_snapshot_identity TEXT NOT NULL,
                CHECK (length(prefix) > 0),
                CHECK (length(validated_snapshot_identity) > 0)
            );
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

    private static bool HasDirtyRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chunk_index_entries WHERE dirty = 1 LIMIT 1);";
        return command.ExecuteScalar() is long count && count != 0;
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction, bool dirty, bool preserveDirtyRows)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = preserveDirtyRows
            ? """
                INSERT INTO chunk_index_entries(content_hash, chunk_hash, original_size, compressed_size, prefix, dirty)
                VALUES ($contentHash, $chunkHash, $originalSize, $compressedSize, $prefix, $dirty)
                ON CONFLICT(content_hash) DO UPDATE SET
                    chunk_hash = excluded.chunk_hash,
                    original_size = excluded.original_size,
                    compressed_size = excluded.compressed_size,
                    prefix = excluded.prefix,
                    dirty = excluded.dirty
                WHERE chunk_index_entries.dirty = 0;
                """
            : """
                INSERT INTO chunk_index_entries(content_hash, chunk_hash, original_size, compressed_size, prefix, dirty)
                VALUES ($contentHash, $chunkHash, $originalSize, $compressedSize, $prefix, $dirty)
                ON CONFLICT(content_hash) DO UPDATE SET
                    chunk_hash = excluded.chunk_hash,
                    original_size = excluded.original_size,
                    compressed_size = excluded.compressed_size,
                    prefix = excluded.prefix,
                    dirty = excluded.dirty;
                """;

        command.Parameters.Add("$contentHash", SqliteType.Blob);
        command.Parameters.Add("$chunkHash", SqliteType.Blob);
        command.Parameters.Add("$originalSize", SqliteType.Integer);
        command.Parameters.Add("$compressedSize", SqliteType.Integer);
        command.Parameters.Add("$prefix", SqliteType.Text);
        command.Parameters.Add("$dirty", SqliteType.Integer).Value = dirty ? 1 : 0;
        return command;
    }

    private static void BindEntry(SqliteCommand command, ShardEntry entry)
    {
        command.Parameters["$contentHash"].Value = ParseHashBytes(entry.ContentHash.ToString());
        command.Parameters["$chunkHash"].Value = ParseHashBytes(entry.ChunkHash.ToString());
        command.Parameters["$originalSize"].Value = entry.OriginalSize;
        command.Parameters["$compressedSize"].Value = entry.CompressedSize;
        command.Parameters["$prefix"].Value = ChunkIndexRouter.GetLeafPrefix(entry.ContentHash).ToString();
    }

    private static int UpsertLoadedPrefix(SqliteConnection connection, SqliteTransaction transaction, LoadedPrefixState loadedPrefix)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO loaded_prefixes(prefix, remote_exists, remote_blob_identity, validated_snapshot_identity)
            VALUES ($prefix, $remoteExists, $remoteBlobIdentity, $validatedSnapshotIdentity)
            ON CONFLICT(prefix) DO UPDATE SET
                remote_exists = excluded.remote_exists,
                remote_blob_identity = excluded.remote_blob_identity,
                validated_snapshot_identity = excluded.validated_snapshot_identity;
            """;
        command.Parameters.AddWithValue("$prefix", loadedPrefix.Prefix.ToString());
        command.Parameters.Add("$remoteExists", SqliteType.Integer).Value = loadedPrefix.RemoteExists ? 1 : 0;
        command.Parameters.Add("$remoteBlobIdentity", SqliteType.Text).Value = (object?)loadedPrefix.RemoteBlobIdentity ?? DBNull.Value;
        command.Parameters.AddWithValue("$validatedSnapshotIdentity", loadedPrefix.ValidatedSnapshotIdentity);
        return command.ExecuteNonQuery();
    }

    private static ShardEntry ReadEntry(SqliteDataReader reader)
        => new(
            ContentHash.FromDigest((byte[])reader.GetValue(0)),
            ChunkHash.FromDigest((byte[])reader.GetValue(1)),
            reader.GetInt64(2),
            reader.GetInt64(3));

    private static byte[] ParseHashBytes(string value)
        => Convert.FromHexString(value);

    // -- FILE MARKERS ---------------------------------------------------------

    private bool WriteDirtyMarker()
    {
        if (!_fileSystem.FileExists(_dirtyMarkerPath))
        {
            _fileSystem.WriteAllBytesAsync(_dirtyMarkerPath, [], CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private bool DeleteDirtyMarker()
    {
        if (_fileSystem.FileExists(_dirtyMarkerPath))
        {
            _fileSystem.DeleteFile(_dirtyMarkerPath);
            return true;
        }

        return false;
    }

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
