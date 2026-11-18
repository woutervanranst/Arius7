using Arius.Api.Features.Repositories;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

public sealed partial class AppDatabase
{
    public IReadOnlyList<RepositoryRecord> ListRepositories()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, alias, container, account_id, local_path, region_hint, passphrase, created_at FROM repositories ORDER BY alias;";
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
        command.CommandText = "SELECT id, alias, container, account_id, local_path, region_hint, passphrase, created_at FROM repositories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRepository(reader) : null;
    }

    public long InsertRepository(string alias, string container, long accountId, string? localPath, string? encryptedPassphrase)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repositories(alias, container, account_id, local_path, passphrase, created_at)
            VALUES ($alias, $container, $accountId, $localPath, $passphrase, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$container", container);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$localPath", (object?)localPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$passphrase", (object?)encryptedPassphrase ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Updates the mutable repository properties. A <c>null</c> argument leaves that column unchanged
    /// (so callers need not resupply secrets that are not being rotated).
    /// </summary>
    public void UpdateRepository(long id, string? alias, string? localPath, string? encryptedPassphrase)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE repositories SET
                alias        = COALESCE($alias, alias),
                local_path   = COALESCE($localPath, local_path),
                passphrase   = COALESCE($passphrase, passphrase)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$alias", (object?)alias ?? DBNull.Value);
        command.Parameters.AddWithValue("$localPath", (object?)localPath ?? DBNull.Value);
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
        // Cascade: a repository's jobs/schedules/cached statistics reference it, so remove them first
        // (download_files hangs off jobs, so it goes before them).
        using (var downloadFiles = connection.CreateCommand())
        {
            downloadFiles.Transaction = transaction;
            downloadFiles.CommandText = "DELETE FROM download_files WHERE job_id IN (SELECT id FROM jobs WHERE repo_id = $id);";
            downloadFiles.Parameters.AddWithValue("$id", id);
            downloadFiles.ExecuteNonQuery();
        }
        foreach (var table in new[] { "jobs", "schedules", "statistics_cache", "repository_defaults" })
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

    private static RepositoryRecord ReadRepository(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),  // region_hint (cached configured region)
        reader.IsDBNull(6) ? null : reader.GetString(6),  // passphrase
        DateTimeOffset.Parse(reader.GetString(7)));
}
