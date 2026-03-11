using System.Text.Json;
using Arius.Core.Models;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Infrastructure;

/// <summary>
/// Optional SQLite metadata cache for an Arius repository.
///
/// Location: <c>~/.arius/cache/{repoId}.db</c>
///
/// The cache is a pure performance optimisation — every query result must be
/// identical to what <see cref="AzureRepository"/> returns without a cache
/// (D10 invariant: <c>result(with_cache) ≡ result(without_cache)</c>).
///
/// Table layout
/// ─────────────
/// blobs     — blob_hash (PK) | pack_id | offset | length | blob_type
/// packs     — pack_id (PK) | first_seen (Unix ms)
/// snapshots — snapshot_id (PK) | time_utc | tree_hash | hostname | username | paths_json | tags_json | parent_id
/// trees     — tree_hash (PK) | nodes_json
/// watermark — id=1  | last_blob_name (lexicographic high-water mark for delta sync)
/// </summary>
public sealed class RepositoryCache : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _db;

    // ── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens (or creates) the cache database for the given repo.
    /// The directory is created automatically if it does not exist.
    /// </summary>
    public static RepositoryCache Open(RepoId repoId)
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".arius", "cache");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{repoId.Value}.db");
        return new RepositoryCache(path);
    }

    /// <summary>Opens (or creates) a cache at an explicit path — useful for tests.</summary>
    public static RepositoryCache OpenAt(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        return new RepositoryCache(dbPath);
    }

    private RepositoryCache(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        ApplySchema();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void ApplySchema()
    {
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS blobs (
                blob_hash TEXT NOT NULL PRIMARY KEY,
                pack_id   TEXT NOT NULL,
                offset    INTEGER NOT NULL,
                length    INTEGER NOT NULL,
                blob_type TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS packs (
                pack_id    TEXT NOT NULL PRIMARY KEY,
                first_seen INTEGER NOT NULL   -- Unix ms
            );

            CREATE TABLE IF NOT EXISTS snapshots (
                snapshot_id TEXT NOT NULL PRIMARY KEY,
                time_utc    TEXT NOT NULL,
                tree_hash   TEXT NOT NULL,
                hostname    TEXT NOT NULL,
                username    TEXT NOT NULL,
                paths_json  TEXT NOT NULL,
                tags_json   TEXT NOT NULL,
                parent_id   TEXT
            );

            CREATE TABLE IF NOT EXISTS trees (
                tree_hash  TEXT NOT NULL PRIMARY KEY,
                nodes_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS watermark (
                id             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                last_blob_name TEXT NOT NULL DEFAULT ''
            );

            INSERT OR IGNORE INTO watermark (id, last_blob_name) VALUES (1, '');

            CREATE INDEX IF NOT EXISTS ix_blobs_pack ON blobs (pack_id);
            """);

        // Ensure schema version row
        var ver = Scalar("SELECT version FROM schema_version LIMIT 1");
        if (ver is null)
            Execute($"INSERT INTO schema_version (version) VALUES ({SchemaVersion})");
    }

    // ── Watermark (delta sync) ────────────────────────────────────────────────

    /// <summary>The lexicographic high-water mark used for delta sync.</summary>
    public string Watermark
    {
        get => (string?)Scalar("SELECT last_blob_name FROM watermark WHERE id=1") ?? "";
        set => Execute("UPDATE watermark SET last_blob_name=@v WHERE id=1",
            ("@v", value));
    }

    // ── Upsert helpers ────────────────────────────────────────────────────────

    public void UpsertBlob(IndexEntry entry)
    {
        Execute("""
            INSERT INTO blobs (blob_hash, pack_id, offset, length, blob_type)
            VALUES (@bh, @pi, @off, @len, @bt)
            ON CONFLICT(blob_hash) DO UPDATE
              SET pack_id=excluded.pack_id,
                  offset=excluded.offset,
                  length=excluded.length,
                  blob_type=excluded.blob_type
            """,
            ("@bh",  entry.BlobHash.Value),
            ("@pi",  entry.PackId.Value),
            ("@off", entry.Offset),
            ("@len", entry.Length),
            ("@bt",  entry.BlobType.ToString()));
    }

    public void UpsertPack(PackId packId)
    {
        Execute("""
            INSERT OR IGNORE INTO packs (pack_id, first_seen)
            VALUES (@pi, @ts)
            """,
            ("@pi", packId.Value),
            ("@ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void UpsertSnapshot(Snapshot snap)
    {
        Execute("""
            INSERT INTO snapshots
              (snapshot_id, time_utc, tree_hash, hostname, username, paths_json, tags_json, parent_id)
            VALUES (@id, @t, @th, @hn, @un, @pj, @tj, @par)
            ON CONFLICT(snapshot_id) DO UPDATE
              SET time_utc=excluded.time_utc,
                  tree_hash=excluded.tree_hash,
                  hostname=excluded.hostname,
                  username=excluded.username,
                  paths_json=excluded.paths_json,
                  tags_json=excluded.tags_json,
                  parent_id=excluded.parent_id
            """,
            ("@id",  snap.Id.Value),
            ("@t",   snap.Time.ToString("O")),
            ("@th",  snap.Tree.Value),
            ("@hn",  snap.Hostname),
            ("@un",  snap.Username),
            ("@pj",  JsonSerializer.Serialize(snap.Paths)),
            ("@tj",  JsonSerializer.Serialize(snap.Tags)),
            ("@par", snap.Parent?.Value as object ?? DBNull.Value));
    }

    public void UpsertTree(TreeHash hash, IReadOnlyList<TreeNode> nodes)
    {
        Execute("""
            INSERT INTO trees (tree_hash, nodes_json)
            VALUES (@th, @nj)
            ON CONFLICT(tree_hash) DO UPDATE SET nodes_json=excluded.nodes_json
            """,
            ("@th", hash.Value),
            ("@nj", JsonSerializer.Serialize(nodes, JsonDefaults.Options)));
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>Returns the <see cref="IndexEntry"/> for a blob hash, or null if not cached.</summary>
    public IndexEntry? FindBlob(BlobHash hash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT pack_id, offset, length, blob_type FROM blobs WHERE blob_hash=@bh";
        cmd.Parameters.AddWithValue("@bh", hash.Value);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new IndexEntry(
            hash,
            new PackId(r.GetString(0)),
            r.GetInt64(1),
            r.GetInt64(2),
            Enum.Parse<BlobType>(r.GetString(3)));
    }

    /// <summary>Returns all cached snapshots, ordered by time ascending.</summary>
    public IReadOnlyList<Snapshot> ListSnapshots()
    {
        var results = new List<Snapshot>();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, time_utc, tree_hash, hostname, username,
                   paths_json, tags_json, parent_id
            FROM snapshots
            ORDER BY time_utc ASC
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new Snapshot(
                new SnapshotId(r.GetString(0)),
                DateTimeOffset.Parse(r.GetString(1)),
                new TreeHash(r.GetString(2)),
                JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [],
                r.GetString(3),
                r.GetString(4),
                JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? [],
                r.IsDBNull(7) ? null : new SnapshotId(r.GetString(7))));
        }

        return results;
    }

    /// <summary>Returns cached tree nodes for the given hash, or null if not cached.</summary>
    public IReadOnlyList<TreeNode>? FindTree(TreeHash hash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT nodes_json FROM trees WHERE tree_hash=@th";
        cmd.Parameters.AddWithValue("@th", hash.Value);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return JsonSerializer.Deserialize<List<TreeNode>>(r.GetString(0), JsonDefaults.Options);
    }

    /// <summary>Loads all blobs into a dictionary (blobHash → entry), for full index rebuilds.</summary>
    public Dictionary<string, IndexEntry> LoadAllBlobs()
    {
        var result = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT blob_hash, pack_id, offset, length, blob_type FROM blobs";

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var entry = new IndexEntry(
                new BlobHash(r.GetString(0)),
                new PackId(r.GetString(1)),
                r.GetInt64(2),
                r.GetInt64(3),
                Enum.Parse<BlobType>(r.GetString(4)));
            result[entry.BlobHash.Value] = entry;
        }

        return result;
    }

    // ── Bulk helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a batch of upserts in a single transaction for performance.
    /// </summary>
    public void InTransaction(Action action)
    {
        using var tx = _db.BeginTransaction();
        action();
        tx.Commit();
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _db.Dispose();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Execute(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}
