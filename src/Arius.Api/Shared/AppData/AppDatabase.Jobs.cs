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

    /// <summary>Transitions a job to a terminal status (<c>completed</c>/<c>failed</c>/<c>cancelled</c>/<c>interrupted</c>).
    /// Guarded: a row already in a terminal status is left untouched — this is what stops a late-arriving cancel
    /// (<see cref="Arius.Api.Shared.Hubs.JobsHub.CancelJob"/>'s fall-through branch) from racing the rehydration poller's
    /// completion and clobbering an already-<c>completed</c> row back to <c>cancelled</c>/pct 0. Returns <c>true</c>
    /// when the guarded UPDATE applied the transition, <c>false</c> when the row was already terminal (guard hit) —
    /// callers that must not act on a no-op transition (e.g. broadcasting a terminal Done) should check this.</summary>
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

    /// <summary>Guarded flip of a parked restore to <c>running</c> for a resume: applies only while the row is still
    /// <c>rehydrating</c>/<c>awaiting-cost</c>. Returns <c>false</c> when the row was terminalized in the meantime
    /// (e.g. a concurrent cancel via <see cref="Arius.Api.Shared.Hubs.JobsHub.CancelJob"/>'s parked branch), so
    /// <see cref="Arius.Api.Shared.Jobs.JobRunner.ResumeRestoreAsync"/> can bail instead of resurrecting a cancelled job to
    /// running and driving it to completion. Unlike <see cref="SetJobStatus"/> this is conditional.</summary>
    public bool TryResumeToRunning(string id, string? detail = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = 'running', detail = $detail WHERE id = $id AND status IN ('rehydrating','awaiting-cost');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        return command.ExecuteNonQuery() > 0;
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
    /// DB every tick so the poller holds no per-job timers and survives an Api restart.</summary>
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

    /// <summary>Jobs stuck at <c>awaiting-cost</c> whose <c>started_at</c> predates <paramref name="olderThan"/> —
    /// the abandoned-cost-prompt work list for <see cref="Arius.Api.Features.Restore.StaleApprovalSweepService"/>. Never returns
    /// <c>rehydrating</c> rows (those legitimately live for hours).</summary>
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

    /// <summary>Latest archive command start time per repository. Unlike <see cref="ListJobs"/>, this is not capped
    /// by the global job-history window, so repository rows always report the actual latest run.</summary>
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

    /// <summary>Non-terminal jobs (running/awaiting-cost/rehydrating), optionally repo-scoped, ordered newest-first.
    /// Deliberately <b>uncapped</b>: unlike <see cref="ListJobs"/> it never hides an active job behind the newest-100
    /// window, since a long-lived <c>awaiting-cost</c> (up to 24h) or <c>rehydrating</c> job can sit outside that
    /// window in a busy multi-repo/scheduled system. Backs <c>GET /jobs?status=active</c> (job-pill discovery).
    /// Bounded in practice by the single-active-job-per-repo unique index.</summary>
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

    /// <summary>Persists the job's live <see cref="JobSnapshot"/> (serialized) so it survives a host restart
    /// and can seed a reconnecting client. Overwritten roughly every progress tick while the job runs.</summary>
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
            "DELETE FROM repository_defaults; DELETE FROM global_defaults; DELETE FROM app_settings; " +
            "DELETE FROM download_files; DELETE FROM jobs; DELETE FROM schedules; DELETE FROM statistics_cache; " +
            "DELETE FROM repositories; DELETE FROM storage_accounts;";
        command.ExecuteNonQuery();
    }

    /// <summary>On Api startup, any job left <c>running</c> OR <c>awaiting-cost</c> by a crash/restart is orphaned:
    /// both carry in-process/in-memory state (the live run, resp. the in-memory approval wait) that is gone after a
    /// restart, so neither can continue in place. Mark them <c>interrupted</c> (terminal → frees the
    /// <c>ux_jobs_one_active_per_repo</c> guard so the user can re-run; no paid work is lost at awaiting-cost because
    /// rehydration hasn't started yet). <c>rehydrating</c> is deliberately left untouched — the poller legitimately
    /// re-arms it from the DB on its next tick.</summary>
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
