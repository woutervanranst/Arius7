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

    // ── Readers ───────────────────────────────────────────────────────────────

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
