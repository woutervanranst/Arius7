using Arius.Api.Features.Statistics;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

/// <summary>
/// The Arius.Api application database, separate from Arius.Core's chunk-index cache. Secrets (account keys,
/// passphrases) are stored as Data Protection ciphertext; see <see cref="SecretProtector"/>.
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

            -- Caches Core's statistics per request variant (version, full-coverage flag). The fingerprint
            -- is the latest snapshot version at compute time and is used to prune older generations.
            -- Reads do not re-check it: the cache is cleared after an archive and on a repository
            -- update or delete.
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

        // CREATE TABLE IF NOT EXISTS does not add columns to an existing table, so later columns are added here.
        EnsureColumn(connection, table: "repositories", column: "region_hint", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "state_json", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "outcome",    type: "TEXT");

        MigrateDefaultTierToRepositoryDefaults(connection);

        // Must run before the unique index is created: an existing database can hold two 'running' rows for one
        // repository, which would make CREATE UNIQUE INDEX fail and abort startup. Any 'running' row at this point
        // is an orphan from a previous process.
        ReconcileRunningJobs(connection);

        // At most one non-terminal job per repository: a new start is rejected, not queued.
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
    /// The serialization version of the <c>statistics_cache</c> payload (the <c>StatisticsDto</c> shape). Bump it
    /// when the payload changes shape, so rows written by an older build are discarded instead of deserialized
    /// with default values.
    /// </summary>
    private const long CachePayloadVersion = 2;

    /// <summary>
    /// Clears <c>statistics_cache</c> once when the payload version stored in <c>PRAGMA user_version</c> is older
    /// than <see cref="CachePayloadVersion"/>. The fingerprint only changes on a new snapshot, so without this a
    /// dormant repository would keep serving old rows with new fields read as 0.
    /// </summary>
    private static void EnsureCachePayloadVersion(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt64(read.ExecuteScalar());
        if (current >= CachePayloadVersion)
            return;

        using var migrate = connection.CreateCommand();
        // PRAGMA does not accept parameters; interpolating the compile-time constant is safe.
        migrate.CommandText = $"DELETE FROM statistics_cache; PRAGMA user_version = {CachePayloadVersion};";
        migrate.ExecuteNonQuery();
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> if it is absent. The names are compile-time
    /// constants, never user input, so interpolating them into DDL is safe.</summary>
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
    /// Moves the legacy <c>repositories.default_tier</c> column into <c>repository_defaults</c>, then drops it.
    /// Unrecognised or blank tiers are dropped so the repository inherits. Runs in one transaction, so a crash leaves
    /// the column in place and the migration reruns on the next start.
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
