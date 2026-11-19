using Arius.Api.Features.Accounts;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

public sealed partial class AppDatabase
{
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

    /// <summary>Replaces the account key. A <c>null</c> <paramref name="encryptedAccountKey"/> leaves the stored key unchanged.</summary>
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

    private static AccountRecord ReadAccount(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)));
}
