using Microsoft.Data.Sqlite;

namespace Arius.Api.AppData;

/// <summary>
/// The Arius.Api application database (separate from Arius.Core's chunk-index cache): storage
/// accounts, repositories, jobs, and schedules. Raw <see cref="SqliteConnection"/> access mirrors
/// the idiom of Arius.Core's local stores (connection-string builder, WAL, parameterized commands).
/// Secrets (account keys, passphrases) are stored as Data-Protection ciphertext; see <see cref="SecretProtector"/>.
/// </summary>
public sealed class AppDatabase
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
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                alias        TEXT NOT NULL,
                container    TEXT NOT NULL,
                account_id   INTEGER NOT NULL REFERENCES storage_accounts(id),
                local_path   TEXT,
                default_tier TEXT NOT NULL DEFAULT 'archive',
                passphrase   TEXT,
                created_at   TEXT NOT NULL,
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
            """;
        create.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // ── Accounts ────────────────────────────────────────────────────────────

    public IReadOnlyList<AccountRecord> ListAccounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, account_key, created_at FROM storage_accounts ORDER BY name;";
        using var reader = command.ExecuteReader();
        var result = new List<AccountRecord>();
        while (reader.Read())
            result.Add(ReadAccount(reader));
        return result;
    }

    public AccountRecord? GetAccount(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, account_key, created_at FROM storage_accounts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public long InsertAccount(string name, string? encryptedAccountKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO storage_accounts(name, account_key, created_at) VALUES ($name, $key, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$key", (object?)encryptedAccountKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return (long)command.ExecuteScalar()!;
    }

    public int CountRepositoriesForAccount(long accountId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repositories WHERE account_id = $accountId;";
        command.Parameters.AddWithValue("$accountId", accountId);
        return (int)(long)command.ExecuteScalar()!;
    }

    // ── Repositories ──────────────────────────────────────────────────────────

    public IReadOnlyList<RepositoryRecord> ListRepositories()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, alias, container, account_id, local_path, default_tier, passphrase, created_at FROM repositories ORDER BY alias;";
        using var reader = command.ExecuteReader();
        var result = new List<RepositoryRecord>();
        while (reader.Read())
            result.Add(ReadRepository(reader));
        return result;
    }

    public RepositoryRecord? GetRepository(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, alias, container, account_id, local_path, default_tier, passphrase, created_at FROM repositories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRepository(reader) : null;
    }

    public long InsertRepository(string alias, string container, long accountId, string? localPath, string defaultTier, string? encryptedPassphrase)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repositories(alias, container, account_id, local_path, default_tier, passphrase, created_at)
            VALUES ($alias, $container, $accountId, $localPath, $defaultTier, $passphrase, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$container", container);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$localPath", (object?)localPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$defaultTier", defaultTier);
        command.Parameters.AddWithValue("$passphrase", (object?)encryptedPassphrase ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Updates the mutable repository properties. A <c>null</c> argument leaves that column unchanged
    /// (so callers need not resupply secrets that are not being rotated).
    /// </summary>
    public void UpdateRepository(long id, string? alias, string? localPath, string? defaultTier, string? encryptedPassphrase)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE repositories SET
                alias        = COALESCE($alias, alias),
                local_path   = COALESCE($localPath, local_path),
                default_tier = COALESCE($defaultTier, default_tier),
                passphrase   = COALESCE($passphrase, passphrase)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$alias", (object?)alias ?? DBNull.Value);
        command.Parameters.AddWithValue("$localPath", (object?)localPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$defaultTier", (object?)defaultTier ?? DBNull.Value);
        command.Parameters.AddWithValue("$passphrase", (object?)encryptedPassphrase ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void DeleteRepository(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM repositories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public void InsertJob(string id, long repositoryId, string kind, string trigger, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(id, repo_id, kind, trigger, status, pct, started_at)
            VALUES ($id, $repoId, $kind, $trigger, $status, 0, $startedAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$repoId", repositoryId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$trigger", trigger);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void CompleteJob(string id, string status, double pct, string? detail)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $status, pct = $pct, detail = $detail, finished_at = $finishedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$pct", pct);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JobRecord> ListJobs(int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at FROM jobs ORDER BY COALESCE(started_at, '') DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }

    // ── Schedules ───────────────────────────────────────────────────────────

    public IReadOnlyList<ScheduleRecord> ListSchedules(long? repositoryId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = repositoryId is null
            ? "SELECT id, repo_id, cron, kind, enabled, next_run FROM schedules ORDER BY id;"
            : "SELECT id, repo_id, cron, kind, enabled, next_run FROM schedules WHERE repo_id = $repoId ORDER BY id;";
        if (repositoryId is not null) command.Parameters.AddWithValue("$repoId", repositoryId);
        using var reader = command.ExecuteReader();
        var result = new List<ScheduleRecord>();
        while (reader.Read())
            result.Add(ReadSchedule(reader));
        return result;
    }

    public long InsertSchedule(long repositoryId, string cron, string kind, bool enabled)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO schedules(repo_id, cron, kind, enabled) VALUES ($repoId, $cron, $kind, $enabled);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$repoId", repositoryId);
        command.Parameters.AddWithValue("$cron", cron);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        return (long)command.ExecuteScalar()!;
    }

    public void SetScheduleNextRun(long id, DateTimeOffset? nextRun)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE schedules SET next_run = $nextRun WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$nextRun", (object?)nextRun?.ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void DeleteSchedule(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schedules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ── Readers ───────────────────────────────────────────────────────────────

    private static JobRecord ReadJob(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetDouble(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));

    private static ScheduleRecord ReadSchedule(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4) != 0,
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));

    private static AccountRecord ReadAccount(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)));

    private static RepositoryRecord ReadRepository(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7)));
}
