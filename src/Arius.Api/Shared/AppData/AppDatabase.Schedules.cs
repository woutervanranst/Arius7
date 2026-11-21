using Arius.Api.Features.Schedules;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

public sealed partial class AppDatabase
{
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

    private static ScheduleRecord ReadSchedule(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4) != 0,
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
}
