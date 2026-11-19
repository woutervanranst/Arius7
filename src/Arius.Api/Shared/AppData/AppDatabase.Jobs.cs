using Arius.Api.Features.Jobs;
using Arius.Api.Shared.Jobs;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

public sealed partial class AppDatabase
{
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

    /// <summary>Moves a job to a terminal status. A row that is already terminal is left unchanged, so a late cancel
    /// cannot overwrite a job the rehydration poller has just completed. Returns <c>false</c> when the row was already
    /// terminal; callers that act on the transition (e.g. broadcasting <c>Done</c>) must check it.</summary>
    public bool CompleteJob(string id, string status, double pct, string? detail)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $status, pct = $pct, detail = $detail, finished_at = $finishedAt WHERE id = $id AND status NOT IN (" + JobStatuses.TerminalSqlList + ");";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$pct", pct);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Moves a job between non-terminal statuses (<c>running</c>, <c>awaiting-cost</c>, <c>rehydrating</c>)
    /// without setting <c>finished_at</c>; use <see cref="CompleteJob"/> for terminal statuses.</summary>
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

    /// <summary>Moves a parked restore (<c>rehydrating</c> or <c>awaiting-cost</c>) to <c>running</c> to resume it.
    /// Returns <c>false</c> when the job became terminal in the meantime (e.g. it was cancelled), so
    /// <see cref="Arius.Api.Shared.Jobs.JobRunner.ResumeRestoreAsync"/> does not resume a cancelled job.</summary>
    public bool TryResumeToRunning(string id, string? detail = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = 'running', detail = $detail WHERE id = $id AND status IN ('rehydrating','awaiting-cost');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        return command.ExecuteNonQuery() > 0;
    }

    public JobRecord? GetJob(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>Jobs in <c>rehydrating</c>. The rehydration poller reads this every tick instead of keeping per-job
    /// timers, so it survives an Api restart.</summary>
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

    /// <summary>Jobs at <c>awaiting-cost</c> that started before <paramref name="olderThan"/>: the abandoned cost
    /// prompts that <see cref="Arius.Api.Features.Restore.StaleApprovalSweepService"/> declines.</summary>
    public IReadOnlyList<JobRecord> ListStaleAwaitingCost(DateTimeOffset olderThan)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs WHERE status = 'awaiting-cost' AND started_at < $t;";
        command.Parameters.AddWithValue("$t", olderThan.ToString("O"));
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

    /// <summary>Latest archive start time per repository. Unlike <see cref="ListJobs"/> it is not limited to the
    /// recent job history, so it always reports the latest run.</summary>
    public IReadOnlyDictionary<long, DateTimeOffset> ListLatestArchiveStartedAt(long? repositoryId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT repo_id, MAX(started_at)
            FROM jobs
            WHERE kind = 'archive' AND started_at IS NOT NULL
            """ + (repositoryId is null ? "" : " AND repo_id = $repositoryId") + " GROUP BY repo_id;";
        if (repositoryId is not null)
            command.Parameters.AddWithValue("$repositoryId", repositoryId.Value);

        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, DateTimeOffset>();
        while (reader.Read())
            result[reader.GetInt64(0)] = DateTimeOffset.Parse(reader.GetString(1));
        return result;
    }

    /// <summary>Non-terminal jobs, newest first, optionally for one repository. Unlike <see cref="ListJobs"/> it has no
    /// limit, so a long-lived <c>awaiting-cost</c> or <c>rehydrating</c> job is never hidden behind newer jobs. The
    /// single-active-job index bounds it to one row per repository.</summary>
    public IReadOnlyList<JobRecord> ListActiveJobs(long? repositoryId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome " +
            $"FROM jobs WHERE status IN ({JobStatuses.NonTerminalSqlList})" +
            (repositoryId is null ? "" : " AND repo_id = $r") +
            " ORDER BY COALESCE(started_at, '') DESC;";
        if (repositoryId is not null) command.Parameters.AddWithValue("$r", repositoryId);
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }

    /// <summary>Persists the job's serialized <see cref="JobSnapshot"/> so it survives a host restart and can seed a
    /// reconnecting client.</summary>
    public void SaveJobState(string id, string stateJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET state_json = $s WHERE id = $id;";
        command.Parameters.AddWithValue("$s", stateJson);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Persists the job's serialized terminal <see cref="JobOutcome"/> for the job history.</summary>
    public void SetJobOutcome(string id, string outcomeJson)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET outcome = $o WHERE id = $id;";
        command.Parameters.AddWithValue("$o", outcomeJson);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Whether the repository has a non-terminal job. This check is cooperative; the
    /// <c>ux_jobs_one_active_per_repo</c> unique index is the race-proof backstop.</summary>
    public bool HasActiveJob(long repositoryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM jobs WHERE repo_id = $r AND status IN (" + JobStatuses.NonTerminalSqlList + ") LIMIT 1;";
        command.Parameters.AddWithValue("$r", repositoryId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Test-only: deletes every row so the hermetic browser specs are isolated from each other.</summary>
    public void ResetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM repository_defaults; DELETE FROM global_defaults; DELETE FROM app_settings; " +
            "DELETE FROM download_files; DELETE FROM jobs; DELETE FROM schedules; DELETE FROM statistics_cache; " +
            "DELETE FROM repositories; DELETE FROM storage_accounts;";
        command.ExecuteNonQuery();
    }

    /// <summary>Marks jobs left <c>running</c> or <c>awaiting-cost</c> by a crash or restart as <c>interrupted</c>:
    /// their live run or in-memory approval wait is gone, so they cannot continue. This frees the single-active-job
    /// guard; no paid work is lost at <c>awaiting-cost</c> because rehydration has not started. <c>rehydrating</c> jobs
    /// are left for the rehydration poller to resume.</summary>
    public int ReconcileInterruptedJobs()
    {
        using var connection = OpenConnection();
        return ReconcileRunningJobs(connection);
    }

    /// <summary><see cref="ReconcileInterruptedJobs"/> on an open connection, so <see cref="CreateOrUpgradeSchema"/>
    /// can run it before creating <c>ux_jobs_one_active_per_repo</c>.</summary>
    private static int ReconcileRunningJobs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = 'interrupted', finished_at = $t WHERE status IN ('running','awaiting-cost');";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }

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
}
