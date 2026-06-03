using Microsoft.Data.Sqlite;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// SQLite-backed local chunk-index cache for clean remote shard data and unflushed dirty entries.
/// </summary>
internal sealed class ChunkIndexLocalStore : IDisposable
{
    private const string SchemaVersion = "1";

    private readonly RelativeFileSystem _fileSystem;
    private readonly LocalDirectory     _rootDirectory;
    private readonly RelativePath       _databasePath    = RelativePath.Root / PathSegment.Parse("cache.sqlite");
    private readonly RelativePath       _dirtyMarkerPath = RelativePath.Root / PathSegment.Parse("dirty.marker");
    private readonly string             _connectionString;
    private readonly Lock               _localStateGate = new();

    // -- LIFECYCLE ------------------------------------------------------------

    /// <summary>
    /// Opens the local store rooted at <paramref name="root"/> and ensures the SQLite schema exists.
    /// </summary>
    public ChunkIndexLocalStore(LocalDirectory root)
    {
        _rootDirectory = root;
        _fileSystem    = new RelativeFileSystem(root);
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

    /// <summary>
    /// Releases resources held by the store.
    /// </summary>
    public void Dispose()
    {
    }

    // -- LOOKUP ---------------------------------------------------------------

    /// <summary>
    /// Returns the chunk-index entry for <paramref name="contentHash"/>, ignoring whether it is dirty.
    /// </summary>
    public ShardEntry? GetValueOrDefault(ContentHash contentHash)
    {
        var row = GetRowOrDefault(contentHash);
        return row?.Entry;
    }

    /// <summary>
    /// Returns the local row for <paramref name="contentHash"/>, including its dirty state.
    /// </summary>
    public LocalStoreRow? GetRowOrDefault(ContentHash contentHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash, chunk_hash, original_size, compressed_size, dirty FROM chunk_index_entries WHERE content_hash = $contentHash;";
        command.Parameters.Add("$contentHash", SqliteType.Blob).Value = ParseHashBytes(contentHash.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>
    /// Returns the cached remote-validation state for <paramref name="prefix"/>, if it is known locally.
    /// </summary>
    public LoadedPrefixState? GetLoadedPrefixState(PathSegment prefix)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remote_exists, remote_blob_identity, validated_snapshot_identity FROM loaded_prefixes WHERE prefix = $prefix;";
        command.Parameters.AddWithValue("$prefix", prefix.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new LoadedPrefixState(
            prefix,
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    /// <summary>
    /// Returns prefixes that contain dirty entries which still need to be flushed to remote shard blobs.
    /// </summary>
    public IReadOnlyList<PathSegment> GetDirtyPrefixes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT prefix FROM chunk_index_entries WHERE dirty = 1 ORDER BY prefix;";
        using var reader = command.ExecuteReader();
        var prefixes = new List<PathSegment>();
        while (reader.Read())
            prefixes.Add(PathSegment.Parse(reader.GetString(0)));

        return prefixes;
    }

    /// <summary>
    /// Returns prefixes represented by any local chunk-index row.
    /// </summary>
    public IReadOnlyList<PathSegment> GetStoredPrefixes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT prefix FROM chunk_index_entries ORDER BY prefix;";
        using var reader = command.ExecuteReader();
        var prefixes = new List<PathSegment>();
        while (reader.Read())
            prefixes.Add(PathSegment.Parse(reader.GetString(0)));

        return prefixes;
    }

    /// <summary>
    /// Streams all entries for <paramref name="prefix"/> in deterministic content-hash order.
    /// </summary>
    public void ReadPrefixEntries(PathSegment prefix, Action<ShardEntry> consume)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash, chunk_hash, original_size, compressed_size FROM chunk_index_entries WHERE prefix = $prefix ORDER BY content_hash;";
        command.Parameters.AddWithValue("$prefix", prefix.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read())
            consume(ReadEntry(reader));
    }

    /// <summary>
    /// Returns whether the local store currently contains unflushed dirty entries.
    /// </summary>
    public bool HasDirtyRows()
    {
        using var connection = OpenConnection();
        return HasDirtyRows(connection);
    }

    /// <summary>
    /// Returns whether the crash-safety marker indicates that dirty rows may exist.
    /// </summary>
    public bool HasDirtyMarker() => _fileSystem.FileExists(_dirtyMarkerPath);

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
        var materialized = entries.ToArray();
        if (materialized.Length == 0)
            return;

        lock (_localStateGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = CreateUpsertCommand(connection, transaction, dirty: true, preserveDirtyRows: false);
            foreach (var entry in materialized)
            {
                BindEntry(command, entry);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            WriteDirtyMarker();
        }
    }

    /// <summary>
    /// Marks dirty rows in the provided prefixes as clean after their shards have been uploaded.
    /// </summary>
    public void MarkDirtyPrefixesClean(IReadOnlyCollection<PathSegment> prefixes)
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
            foreach (var prefix in prefixes)
            {
                prefixParameter.Value = prefix.ToString();
                command.ExecuteNonQuery();
            }

            transaction.Commit();

            if (!HasDirtyRows(connection))
                DeleteDirtyMarker();
        }
    }

    // -- CLEAN CACHE ----------------------------------------------------------

    /// <summary>
    /// Records a known-clean entry, typically during explicit repair from remote chunk blobs.
    /// </summary>
    public void UpsertClean(ShardEntry entry)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = CreateUpsertCommand(connection, transaction, dirty: false, preserveDirtyRows: false);
        BindEntry(command, entry);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Replaces local clean rows for a loaded prefix while preserving any dirty rows for the same content hashes.
    /// </summary>
    public void IngestCleanPrefix(LoadedPrefixState loadedPrefix, IEnumerable<ShardEntry> entries)
    {
        var materialized = entries.ToArray();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = CreateUpsertCommand(connection, transaction, dirty: false, preserveDirtyRows: true);
        foreach (var entry in materialized)
        {
            BindEntry(command, entry);
            command.ExecuteNonQuery();
        }

        UpsertLoadedPrefix(connection, transaction, loadedPrefix);
        transaction.Commit();
    }

    /// <summary>
    /// Deletes clean cached rows and validation state for <paramref name="prefix"/> while preserving dirty rows.
    /// </summary>
    public void DeleteCleanPrefix(PathSegment prefix)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chunk_index_entries WHERE prefix = $prefix AND dirty = 0; DELETE FROM loaded_prefixes WHERE prefix = $prefix;";
        command.Parameters.AddWithValue("$prefix", prefix.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the cached remote-validation state for a prefix.
    /// </summary>
    public void UpdateLoadedPrefixState(LoadedPrefixState loadedPrefix)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertLoadedPrefix(connection, transaction, loadedPrefix);
        transaction.Commit();
    }

    /// <summary>
    /// Clears all disposable clean cache state while preserving unflushed dirty rows.
    /// </summary>
    public void ClearCleanCache()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deleteEntries = connection.CreateCommand();
        deleteEntries.Transaction = transaction;
        deleteEntries.CommandText = "DELETE FROM chunk_index_entries WHERE dirty = 0;";
        deleteEntries.ExecuteNonQuery();

        using var deletePrefixes = connection.CreateCommand();
        deletePrefixes.Transaction = transaction;
        deletePrefixes.CommandText = "DELETE FROM loaded_prefixes;";
        deletePrefixes.ExecuteNonQuery();
        transaction.Commit();

        DeleteLegacyShardCacheFiles();
    }

    // -- RECOVERY -------------------------------------------------------------

    /// <summary>
    /// Recreates the SQLite database after local-store corruption, optionally keeping backup files for inspection.
    /// </summary>
    public void RecreateDatabase(bool backupExisting)
    {
        lock (_localStateGate)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = backupExisting
                    ? RelativePath.Parse($"cache.sqlite{suffix}.bak")
                    : default;
                var current = RelativePath.Parse($"cache.sqlite{suffix}");
                if (!_fileSystem.FileExists(current))
                    continue;

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

            DeleteDirtyMarker();
            Initialize();
        }
    }

    // -- SQLITE HELPERS -------------------------------------------------------

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

    private SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction, bool dirty, bool preserveDirtyRows)
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

    private static void UpsertLoadedPrefix(SqliteConnection connection, SqliteTransaction transaction, LoadedPrefixState loadedPrefix)
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
        command.ExecuteNonQuery();
    }

    private static ShardEntry ReadEntry(SqliteDataReader reader)
        => new(
            ContentHash.FromDigest((byte[])reader.GetValue(0)),
            ChunkHash.FromDigest((byte[])reader.GetValue(1)),
            reader.GetInt64(2),
            reader.GetInt64(3));

    private static LocalStoreRow ReadRow(SqliteDataReader reader)
        => new(ReadEntry(reader), reader.GetInt64(4) != 0);

    private static byte[] ParseHashBytes(string value)
        => Convert.FromHexString(value);

    // -- FILE MARKERS ---------------------------------------------------------

    private void WriteDirtyMarker()
    {
        if (!_fileSystem.FileExists(_dirtyMarkerPath))
            _fileSystem.WriteAllBytesAsync(_dirtyMarkerPath, [], CancellationToken.None).GetAwaiter().GetResult();
    }

    private void DeleteDirtyMarker()
    {
        if (_fileSystem.FileExists(_dirtyMarkerPath))
            _fileSystem.DeleteFile(_dirtyMarkerPath);
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

    internal sealed record LocalStoreRow(ShardEntry Entry, bool IsDirty);
}
