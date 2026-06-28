using Arius.Core.Shared.ChunkIndex;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests.Shared.ChunkIndex;

/// <summary>
/// Test-only probes of <see cref="ChunkIndexLocalStore"/> persisted state. These query the SQLite cache directly
/// (via the store's <see cref="ChunkIndexLocalStore.ConnectionString"/> seam) so no production caller has to expose
/// them — production resolves coverage through FindCoveredPrefixes / IsPrefixAtETag instead.
/// </summary>
internal static class ChunkIndexLocalStoreTestExtensions
{
    /// <summary>Whether <paramref name="prefix"/> is recorded as validated at <paramref name="snapshotVersion"/> in loaded_prefixes.</summary>
    public static bool IsPrefixAtSnapshotVersion(this ChunkIndexLocalStore store, PathSegment prefix, string snapshotVersion)
    {
        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM loaded_prefixes WHERE prefix = $prefix AND snapshot_version = $snapshotVersion LIMIT 1);";
        command.Parameters.AddWithValue("$prefix", prefix.ToString());
        command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion);
        return command.ExecuteScalar() is long value && value != 0;
    }
}
