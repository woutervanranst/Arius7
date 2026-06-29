using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Core.Shared.HashCache;

/// <summary>
/// SQLite-backed local hashcache: maps a repository-relative path to the cheap change-signals,
/// sparse fingerprint, and cached content hash captured the last time the file was hashed.
/// A disposable accelerator — losing it costs a full-hash run, never data.
/// </summary>
[SharedWithinAssembly]
internal sealed class HashCacheLocalStore
{
    private const string SchemaVersion = "1";

    private readonly RelativePath _databasePath = RelativePath.Root / PathSegment.Parse("cache.sqlite");
    private readonly LocalDirectory                _root;
    private readonly ILogger<HashCacheLocalStore>  _logger;
    private readonly string                        _connectionString;
    private readonly Lock                          _gate = new();

    public HashCacheLocalStore(LocalDirectory root, ILogger<HashCacheLocalStore>? logger = null)
    {
        _root   = root;
        _logger = logger ?? NullLogger<HashCacheLocalStore>.Instance;

        new RelativeFileSystem(root).CreateDirectory(RelativePath.Root);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(_databasePath),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString();

        CreateOrUpgradeSchema();
    }

    internal string ConnectionString => _connectionString;

    /// <summary>Test seam: reads PRAGMA synchronous on a store-produced connection (3=EXTRA,2=FULL,1=NORMAL,0=OFF).</summary>
    internal int QuerySynchronous()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public HashCacheEntry? Find(RelativePath path)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT size, mtime, ctime, inode, dev, signal_set, sparse_fp, fp_algo, content_hash, last_verified
            FROM file_hashes WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new HashCacheEntry(
            Path:              path,
            Size:              reader.GetInt64(0),
            MtimeTicks:        reader.GetInt64(1),
            CtimeTicks:        reader.IsDBNull(2) ? null : reader.GetInt64(2),
            Inode:             reader.IsDBNull(3) ? null : reader.GetString(3),
            Dev:               reader.IsDBNull(4) ? null : reader.GetString(4),
            SignalSet:         reader.GetInt32(5),
            SparseFingerprint: (byte[])reader[6],
            FpAlgo:            reader.GetInt32(7),
            ContentHash:       ContentHash.Parse(reader.GetString(8)),
            LastVerifiedTicks: reader.GetInt64(9));
    }

    public void Upsert(HashCacheEntry entry)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO file_hashes (path, size, mtime, ctime, inode, dev, signal_set, sparse_fp, fp_algo, content_hash, last_verified)
                VALUES ($path, $size, $mtime, $ctime, $inode, $dev, $signal_set, $sparse_fp, $fp_algo, $content_hash, $last_verified)
                ON CONFLICT(path) DO UPDATE SET
                    size=excluded.size, mtime=excluded.mtime, ctime=excluded.ctime, inode=excluded.inode,
                    dev=excluded.dev, signal_set=excluded.signal_set, sparse_fp=excluded.sparse_fp,
                    fp_algo=excluded.fp_algo, content_hash=excluded.content_hash, last_verified=excluded.last_verified;
                """;
            command.Parameters.AddWithValue("$path", entry.Path.ToString());
            command.Parameters.AddWithValue("$size", entry.Size);
            command.Parameters.AddWithValue("$mtime", entry.MtimeTicks);
            command.Parameters.AddWithValue("$ctime", (object?)entry.CtimeTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$inode", (object?)entry.Inode ?? DBNull.Value);
            command.Parameters.AddWithValue("$dev", (object?)entry.Dev ?? DBNull.Value);
            command.Parameters.AddWithValue("$signal_set", entry.SignalSet);
            command.Parameters.Add("$sparse_fp", SqliteType.Blob).Value = entry.SparseFingerprint;
            command.Parameters.AddWithValue("$fp_algo", entry.FpAlgo);
            command.Parameters.AddWithValue("$content_hash", entry.ContentHash.ToString());
            command.Parameters.AddWithValue("$last_verified", entry.LastVerifiedTicks);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(RelativePath path)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM file_hashes WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path.ToString());
            command.ExecuteNonQuery();
        }
    }

    private void CreateOrUpgradeSchema()
    {
        using var connection = OpenConnection();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = wal;";
            pragma.ExecuteNonQuery();
        }

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_hashes (
                path           TEXT    NOT NULL PRIMARY KEY,
                size           INTEGER NOT NULL CHECK (size >= 0),
                mtime          INTEGER NOT NULL,
                ctime          INTEGER,
                inode          TEXT,
                dev            TEXT,
                signal_set     INTEGER NOT NULL,
                sparse_fp      BLOB    NOT NULL,
                fp_algo        INTEGER NOT NULL,
                content_hash   TEXT    NOT NULL,
                last_verified  INTEGER NOT NULL
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
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = normal;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
