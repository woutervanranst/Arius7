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
                region_hint  TEXT,
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
            """;
        create.ExecuteNonQuery();

        // Additive migration for databases created before region_hint existed (CREATE TABLE IF NOT EXISTS
        // won't add a column to an existing table). Idempotent: no-op once the column is present.
        EnsureColumn(connection, table: "repositories", column: "region_hint", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "state_json", type: "TEXT");
        EnsureColumn(connection, table: "jobs", column: "outcome",    type: "TEXT");

        // Reconcile orphaned "running" jobs BEFORE creating the unique index below. A database created
        // before this guard existed can already hold two 'running' rows for the same repo (the pre-guard
        // code inserted status='running' before taking the per-repo gate), which would make the CREATE
        // UNIQUE INDEX fail with a constraint violation and fault AppDatabase construction — and with it,
        // Api startup. A 'running' row found here is always an orphan from a prior process (this
        // constructor is the only writer active right now), so reconciling first is always correct.
        ReconcileRunningJobs(connection);

        // Enforces at most one non-terminal job per repository (running | awaiting-cost | rehydrating);
        // a new start is rejected rather than queued. Runs on every startup (IF NOT EXISTS), so it also
        // lands on databases created before this index existed.
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
    /// than silently deserialized with default values. v2 added per-tier and total storage-cost fields.
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

    /// <summary>
    /// Updates an account's connection material. A <c>null</c> <paramref name="encryptedAccountKey"/> leaves the
    /// stored key unchanged (so the key need not be resupplied for an unrelated edit).
    /// </summary>
    public void UpdateAccount(long id, string? encryptedAccountKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE storage_accounts SET
                account_key = COALESCE($key, account_key)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$key", (object?)encryptedAccountKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void DeleteAccount(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM storage_accounts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int CountRepositoriesForAccount(long accountId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repositories WHERE account_id = $accountId;";
        command.Parameters.AddWithValue("$accountId", accountId);
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>Repository ids belonging to an account — used to evict providers / clear stats caches after an account change.</summary>
    public IReadOnlyList<long> ListRepositoryIdsForAccount(long accountId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories WHERE account_id = $accountId;";
        command.Parameters.AddWithValue("$accountId", accountId);
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
            result.Add(reader.GetInt64(0));
        return result;
    }

    // ── Repositories ──────────────────────────────────────────────────────────

    public IReadOnlyList<RepositoryRecord> ListRepositories()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, alias, container, account_id, local_path, default_tier, region_hint, passphrase, created_at FROM repositories ORDER BY alias;";
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
        command.CommandText = "SELECT id, alias, container, account_id, local_path, default_tier, region_hint, passphrase, created_at FROM repositories WHERE id = $id;";
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

    /// <summary>
    /// Caches a repository's configured region (<see cref="RepositoryRecord.RegionHint"/>). Pass a non-null,
    /// configured region to memoize it (it's immutable once set, so the overview can serve it without opening
    /// the container); pass <c>null</c> to invalidate the cache so the region is re-resolved live on next read.
    /// </summary>
    public void SetRepositoryRegionHint(long id, string? regionHint)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE repositories SET region_hint = $regionHint WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$regionHint", (object?)regionHint ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void DeleteRepository(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        // Cascade: a repository's jobs/schedules/cached statistics reference it, so remove them first.
        foreach (var table in new[] { "jobs", "schedules", "statistics_cache" })
        {
            using var child = connection.CreateCommand();
            child.Transaction = transaction;
            child.CommandText = $"DELETE FROM {table} WHERE repo_id = $id;";
            child.Parameters.AddWithValue("$id", id);
            child.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM repositories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
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

    /// <summary>Transitions a job to a terminal status (<c>completed</c>/<c>failed</c>/<c>cancelled</c>/<c>interrupted</c>).
    /// Guarded: a row already in a terminal status is left untouched — this is what stops a late-arriving cancel
    /// (<see cref="Arius.Api.Hubs.JobsHub.CancelJob"/>'s fall-through branch) from racing the rehydration poller's
    /// completion and clobbering an already-<c>completed</c> row back to <c>cancelled</c>/pct 0.</summary>
    public void CompleteJob(string id, string status, double pct, string? detail)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $status, pct = $pct, detail = $detail, finished_at = $finishedAt WHERE id = $id AND status NOT IN (" + JobStatuses.TerminalSqlList + ");";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$pct", pct);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Updates a job's <c>status</c> (and optional <c>detail</c>) for a NON-terminal transition
    /// (running↔awaiting-cost↔rehydrating). Leaves <c>finished_at</c> untouched — use <see cref="CompleteJob"/>
    /// for terminal states. The <c>ux_jobs_one_active_per_repo</c> index is enforced: moving between two
    /// non-terminal statuses for the same repository's single active row is a plain UPDATE and never conflicts.</summary>
    public void SetJobStatus(string id, string status, string? detail = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = detail is null
            ? "UPDATE jobs SET status = $status WHERE id = $id;"
            : "UPDATE jobs SET status = $status, detail = $detail WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        if (detail is not null) command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    /// <summary>Reads a single job by id, or <c>null</c> if it does not exist. Backs <c>GET /jobs/{id}</c>
    /// and the rehydration poller's per-job due check.</summary>
    public JobRecord? GetJob(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>All jobs currently in <c>rehydrating</c> — the rehydration poller's work list. Rebuilt from the
    /// DB every tick so the poller holds no per-job timers and survives an Api restart (design §7).</summary>
    public IReadOnlyList<JobRecord> ListActiveRehydrations()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE status = 'rehydrating';";
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }

    public IReadOnlyList<JobRecord> ListJobs(int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs ORDER BY COALESCE(started_at, '') DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }

    /// <summary>Persists the job's live <see cref="JobSnapshot"/> (serialized) so it survives a host restart
    /// and can seed a reconnecting client (Task 7). Overwritten roughly every progress tick while the job runs.</summary>
    public void SaveJobState(string id, string stateJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET state_json = $s WHERE id = $id;";
        command.Parameters.AddWithValue("$s", stateJson);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Persists the job's terminal <see cref="JobOutcome"/> (serialized) for the jobs-history list.</summary>
    public void SetJobOutcome(string id, string outcomeJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET outcome = $o WHERE id = $id;";
        command.Parameters.AddWithValue("$o", outcomeJson);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Cooperative check backing the single-active-job-per-repository guard: <c>true</c> while the
    /// repository has a non-terminal job (running/awaiting-cost/rehydrating). The <c>ux_jobs_one_active_per_repo</c>
    /// unique index is the race-proof backstop for callers that race past this check.</summary>
    public bool HasActiveJob(long repositoryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM jobs WHERE repo_id = $r AND status IN (" + JobStatuses.NonTerminalSqlList + ") LIMIT 1;";
        command.Parameters.AddWithValue("$r", repositoryId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Test-only: wipes every app row (accounts/repositories/jobs/schedules/statistics) for cross-spec
    /// isolation in the hermetic browser suite. Never called by production paths.</summary>
    public void ResetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM jobs; DELETE FROM schedules; DELETE FROM statistics_cache; " +
            "DELETE FROM repositories; DELETE FROM storage_accounts;";
        command.ExecuteNonQuery();
    }

    /// <summary>On Api startup, any job left <c>running</c> OR <c>awaiting-cost</c> by a crash/restart is orphaned:
    /// both carry in-process/in-memory state (the live run, resp. the in-memory approval wait) that is gone after a
    /// restart, so neither can continue in place. Mark them <c>interrupted</c> (terminal → frees the
    /// <c>ux_jobs_one_active_per_repo</c> guard so the user can re-run; no paid work is lost at awaiting-cost because
    /// rehydration hasn't started yet). <c>rehydrating</c> is deliberately left untouched — the poller legitimately
    /// re-arms it from the DB on its next tick. Redundant after construction — <see cref="CreateOrUpgradeSchema"/>
    /// already runs this (via <see cref="ReconcileRunningJobs"/>) before the unique index is created — but kept public
    /// because it's semantically meaningful on its own and is exercised directly by tests.</summary>
    public int ReconcileInterruptedJobs()
    {
        using var connection = OpenConnection();
        return ReconcileRunningJobs(connection);
    }

    /// <summary>Marks every orphaned <c>running</c> AND <c>awaiting-cost</c> job <c>interrupted</c> on the given
    /// connection (both have in-process/in-memory state that's gone after a restart). <c>rehydrating</c> is left for
    /// the poller to re-arm. Shared by the schema initializer (must run before <c>ux_jobs_one_active_per_repo</c> is
    /// created — see the call site in <see cref="CreateOrUpgradeSchema"/>) and the public
    /// <see cref="ReconcileInterruptedJobs"/>.</summary>
    private static int ReconcileRunningJobs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = 'interrupted', finished_at = $t WHERE status IN ('running','awaiting-cost');";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
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

    // ── Statistics cache ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached statistics <c>payload</c> for a request variant, or <c>null</c> if not cached.
    /// This is a pure local read — no blob storage is touched — so a hit is fast. Freshness is the
    /// caller's responsibility: the cache is invalidated explicitly (see <see cref="ClearStatisticsCache"/>)
    /// when the snapshot set changes (archive) or the repository's connection changes. The stored
    /// fingerprint records which snapshot the figures were computed against (provenance + prune), but is
    /// deliberately not re-verified against blob storage on every read.
    /// </summary>
    public string? GetCachedStatistics(long repositoryId, string version, bool full)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM statistics_cache WHERE repo_id = $repoId AND version = $version AND full = $full;";
        command.Parameters.AddWithValue("$repoId", repositoryId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$full", full ? 1 : 0);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    /// <summary>
    /// Stores (overwriting any prior variant) the computed statistics <c>payload</c> for a request
    /// variant and prunes the repository's now-stale rows (those stamped with an older fingerprint).
    /// </summary>
    public void UpsertCachedStatistics(long repositoryId, string version, bool full, string fingerprint, string payload)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM statistics_cache WHERE repo_id = $repoId AND fingerprint <> $fingerprint;";
        prune.Parameters.AddWithValue("$repoId", repositoryId);
        prune.Parameters.AddWithValue("$fingerprint", fingerprint);
        prune.ExecuteNonQuery();

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO statistics_cache(repo_id, version, full, fingerprint, payload, created_at)
            VALUES ($repoId, $version, $full, $fingerprint, $payload, $createdAt)
            ON CONFLICT(repo_id, version, full) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                payload     = excluded.payload,
                created_at  = excluded.created_at;
            """;
        upsert.Parameters.AddWithValue("$repoId", repositoryId);
        upsert.Parameters.AddWithValue("$version", version);
        upsert.Parameters.AddWithValue("$full", full ? 1 : 0);
        upsert.Parameters.AddWithValue("$fingerprint", fingerprint);
        upsert.Parameters.AddWithValue("$payload", payload);
        upsert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        upsert.ExecuteNonQuery();

        transaction.Commit();
    }

    /// <summary>Drops all cached statistics for a repository (e.g. after an archive that may have added a snapshot).</summary>
    public void ClearStatisticsCache(long repositoryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM statistics_cache WHERE repo_id = $repoId;";
        command.Parameters.AddWithValue("$repoId", repositoryId);
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
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

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
        reader.IsDBNull(6) ? null : reader.GetString(6),  // region_hint (cached configured region)
        reader.IsDBNull(7) ? null : reader.GetString(7),  // passphrase
        DateTimeOffset.Parse(reader.GetString(8)));
}
