using Microsoft.Data.Sqlite;

namespace Arius.Api.AppData;

public sealed partial class AppDatabase
{
    private const string CleanupEnabledKey = "downloads.cleanupEnabled";
    private const string CleanupDaysKey    = "downloads.cleanupDays";

    /// <summary>Records the files a download job collected, all not-ready and not-downloaded, in one transaction.</summary>
    public void InsertDownloadFiles(string jobId, IReadOnlyList<DownloadFile> files)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO download_files(job_id, path, size) VALUES ($jobId, $path, $size);";
            insert.Parameters.AddWithValue("$jobId", jobId);
            insert.Parameters.AddWithValue("$path", file.Path);
            insert.Parameters.AddWithValue("$size", file.Size);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<DownloadFile> ListDownloadFiles(string jobId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, size, ready, downloaded FROM download_files WHERE job_id = $jobId ORDER BY path;";
        command.Parameters.AddWithValue("$jobId", jobId);
        using var reader = command.ExecuteReader();
        var result = new List<DownloadFile>();
        while (reader.Read())
            result.Add(new DownloadFile(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0));
        return result;
    }

    public void MarkDownloadFileReady(string jobId, string path)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_files SET ready = 1 WHERE job_id = $jobId AND path = $path;";
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    public void MarkDownloadFilesDownloaded(string jobId, IReadOnlyCollection<string> paths)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE download_files SET downloaded = 1 WHERE job_id = $jobId AND path = $path;";
            update.Parameters.AddWithValue("$jobId", jobId);
            update.Parameters.AddWithValue("$path", path);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Jobs of kind <c>download</c> in any status, optionally repo-scoped, newest <c>started_at</c> first.</summary>
    public IReadOnlyList<JobRecord> ListDownloadJobs(long? repositoryId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome " +
            "FROM jobs WHERE kind = 'download'" +
            (repositoryId is null ? "" : " AND repo_id = $r") +
            " ORDER BY COALESCE(started_at, '') DESC;";
        if (repositoryId is not null) command.Parameters.AddWithValue("$r", repositoryId);
        using var reader = command.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
            result.Add(ReadJob(reader));
        return result;
    }

    /// <summary>The server-wide download clean-up setting; absent rows fall back to <see cref="DownloadSettings.Default"/>.</summary>
    public DownloadSettings GetDownloadSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings WHERE key IN ($enabled, $days);";
        command.Parameters.AddWithValue("$enabled", CleanupEnabledKey);
        command.Parameters.AddWithValue("$days", CleanupDaysKey);
        using var reader = command.ExecuteReader();
        var settings = DownloadSettings.Default;
        while (reader.Read())
        {
            var value = reader.GetString(1);
            settings = reader.GetString(0) == CleanupEnabledKey
                ? settings with { CleanupEnabled = value == "true" }
                : settings with { CleanupDays = int.Parse(value) };
        }
        return settings;
    }

    public void SaveDownloadSettings(DownloadSettings settings)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in new[]
        {
            (CleanupEnabledKey, settings.CleanupEnabled ? "true" : "false"),
            (CleanupDaysKey, settings.CleanupDays.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        })
        {
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = "INSERT INTO app_settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            upsert.Parameters.AddWithValue("$key", key);
            upsert.Parameters.AddWithValue("$value", value);
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
