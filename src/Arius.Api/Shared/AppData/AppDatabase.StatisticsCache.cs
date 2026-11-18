using Microsoft.Data.Sqlite;

namespace Arius.Api.AppData;

public sealed partial class AppDatabase
{
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
}
