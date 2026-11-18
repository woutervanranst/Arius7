using Arius.Api.Features.Statistics;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

/// <summary>
/// The Arius.Api application database (separate from Arius.Core's chunk-index cache): storage
/// accounts, repositories, jobs, and schedules. Raw <see cref="SqliteConnection"/> access mirrors
/// the idiom of Arius.Core's local stores (connection-string builder, WAL, parameterized commands).
/// Secrets (account keys, passphrases) are stored as Data-Protection ciphertext; see <see cref="SecretProtector"/>.
/// </summary>
public sealed partial class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString();

        CreateOrUpgradeSchema();
    }

    private void CreateOrUpgradeSchema()
    {
        using var connection = OpenConnection();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = wal; PRAGMA synchronous = normal; PRAGMA foreign_keys = on;";
        pragma.ExecuteNonQuery();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS storage_accounts (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                account_key TEXT,
                created_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repositories (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                alias       TEXT NOT NULL,
                container   TEXT NOT NULL,
                account_id  INTEGER NOT NULL REFERENCES storage_accounts(id),
                local_path  TEXT,
                region_hint TEXT,
                passphrase  TEXT,
                created_at  TEXT NOT NULL,
                UNIQUE(account_id, container)
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id          TEXT PRIMARY KEY,
                repo_id     INTEGER NOT NULL REFERENCES repositories(id),
                kind        TEXT NOT NULL,
                trigger     TEXT NOT NULL,
                status      TEXT NOT NULL,
                pct         REAL NOT NULL DEFAULT 0,
                detail      TEXT,
                started_at  TEXT,
                finished_at TEXT
            );

            CREATE TABLE IF NOT EXISTS schedules (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_id   INTEGER NOT NULL REFERENCES repositories(id),
                cron      TEXT NOT NULL,
                kind      TEXT NOT NULL DEFAULT 'archive',
                enabled   INTEGER NOT NULL DEFAULT 1,
                next_run  TEXT
            );

            -- Archive/restore option defaults (see OptionDefaults). A missing row means "not set at this level".
            CREATE TABLE IF NOT EXISTS global_defaults (option TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS repository_defaults (
                repo_id INTEGER NOT NULL REFERENCES repositories(id),
                option  TEXT NOT NULL,
                value   TEXT NOT NULL,
                PRIMARY KEY (repo_id, option)
            );

            -- Memoizes the (expensive) Arius.Core statistics computation. Keyed by the request
            -- variant (version + full-coverage flag) and stamped with the repository fingerprint —
            -- the latest snapshot version at compute time. A row is a hit only while its fingerprint
            -- still matches the current latest snapshot; a new snapshot changes the fingerprint and
            -- the stale rows are recomputed (and pruned) on the next read.
            CREATE TABLE IF NOT EXISTS statistics_cache (
                repo_id     INTEGER NOT NULL REFERENCES repositories(id),
                version     TEXT NOT NULL,
                full        INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                payload     TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                PRIMARY KEY (repo_id, version, full)
            );

            -- Per-file state of a 'download' job: the repository-relative files it collected, whether each is
            -- staged on the server (ready) and whether the browser has fetched it (downloaded).
            CREATE TABLE IF NOT EXISTS download_files (
                job_id     TEXT    NOT NULL REFERENCES jobs(id),
                path       TEXT    NOT NULL,
                size       INTEGER NOT NULL,
                ready      INTEGER NOT NULL DEFAULT 0,
                downloaded INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (job_id, path)
            );

            -- Server-wide app settings that are not part of the defaults hierarchy (e.g. download clean-up).
            CREATE TABLE IF NOT EXISTS app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        create.ExecuteNonQuery();

        // Additive migration for databases created before region_hint existed (CREATE TABLE IF NOT EXISTS
        // won't add a column to an existing table). Idempotent: no-op once the column is present.
        EnsureColumn(connection, table: "repositories", column: "region_hint", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "state_json", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "outcome",    type: "TEXT");

        MigrateDefaultTierToRepositoryDefaults(connection);

        // Reconcile orphaned "running" jobs BEFORE creating the unique index below. A database can already
        // hold two 'running' rows for the same repo, which would make the CREATE UNIQUE INDEX fail with a
        // constraint violation and fault AppDatabase construction — and with it, Api startup. A 'running'
        // row found here is always an orphan from a prior process (this constructor is the only writer
        // active right now), so reconciling first is always correct.
        ReconcileRunningJobs(connection);

        // Enforces at most one non-terminal job per repository (running | awaiting-cost | rehydrating);
        // a new start is rejected rather than queued. Runs on every startup (IF NOT EXISTS).
        using var index = connection.CreateCommand();
        index.CommandText = $"""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_one_active_per_repo
                ON jobs(repo_id)
                WHERE status IN ({JobStatuses.NonTerminalSqlList});
            """;
        index.ExecuteNonQuery();

        EnsureCachePayloadVersion(connection);
    }

    /// <summary>
    /// The serialization version of the <c>statistics_cache</c> <c>payload</c> (the <c>StatisticsDto</c> shape).
    /// Bump whenever the payload gains/loses fields so stale rows written by an older build are discarded rather
    /// than silently deserialized with default values.
    /// </summary>
    private const long CachePayloadVersion = 2;

    /// <summary>
    /// One-time invalidation of <c>statistics_cache</c> rows whose payload predates <see cref="CachePayloadVersion"/>.
    /// Tracked in <c>PRAGMA user_version</c>: an older build's rows would otherwise deserialize new fields (e.g. the
    /// storage-cost figures) as 0 and keep serving them — the fingerprint guard only refreshes on a snapshot change,
    /// which may never come for a dormant repository. A fresh database has an empty cache, so the clear is a no-op.
    /// </summary>
    private static void EnsureCachePayloadVersion(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt64(read.ExecuteScalar());
        if (current >= CachePayloadVersion)
            return;

        using var migrate = connection.CreateCommand();
        // PRAGMA does not accept parameters; the version is a compile-time integer constant, so this is safe.
        migrate.CommandText = $"DELETE FROM statistics_cache; PRAGMA user_version = {CachePayloadVersion};";
        migrate.ExecuteNonQuery();
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> if it is not already present. Names are
    /// compile-time constants (never user input), so the interpolated DDL is safe.</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        probe.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(probe.ExecuteScalar()) > 0)
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time move of the legacy per-repository <c>default_tier</c> column into <c>repository_defaults</c>:
    /// recognised tiers become explicit repository-level values (blank/unknown ones are dropped so the repository
    /// inherits), then the column is dropped. A no-op once the column is gone. Runs in one transaction, so a crash
    /// leaves the column (and the migration to redo) rather than a half-migrated database.
    /// </summary>
    private static void MigrateDefaultTierToRepositoryDefaults(SqliteConnection connection)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('repositories') WHERE name = 'default_tier';";
        if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
            return;

        using var transaction = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            INSERT OR IGNORE INTO repository_defaults(repo_id, option, value)
                SELECT id, 'tier', lower(trim(default_tier)) FROM repositories
                WHERE lower(trim(default_tier)) IN ('hot', 'cool', 'cold', 'archive');
            ALTER TABLE repositories DROP COLUMN default_tier;
            """;
        migrate.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
