using Microsoft.Data.Sqlite;

namespace Arius.Api.AppData;

public sealed partial class AppDatabase
{
    public DefaultValues GetGlobalDefaults()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT option, value FROM global_defaults;";
        return OptionDefaults.FromRows(ReadOptionRows(command));
    }

    /// <summary>Replaces the whole global set: deletes every row and inserts the non-null values, in one transaction.</summary>
    public void ReplaceGlobalDefaults(DefaultValues values)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM global_defaults;";
        delete.ExecuteNonQuery();
        foreach (var (option, value) in OptionDefaults.ToRows(values))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO global_defaults(option, value) VALUES ($option, $value);";
            insert.Parameters.AddWithValue("$option", option);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public DefaultValues GetRepositoryDefaults(long repositoryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT option, value FROM repository_defaults WHERE repo_id = $repoId;";
        command.Parameters.AddWithValue("$repoId", repositoryId);
        return OptionDefaults.FromRows(ReadOptionRows(command));
    }

    /// <summary>Replaces a repository's whole set: deletes its rows and inserts the non-null values, in one transaction.</summary>
    public void ReplaceRepositoryDefaults(long repositoryId, DefaultValues values)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM repository_defaults WHERE repo_id = $repoId;";
        delete.Parameters.AddWithValue("$repoId", repositoryId);
        delete.ExecuteNonQuery();
        foreach (var (option, value) in OptionDefaults.ToRows(values))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO repository_defaults(repo_id, option, value) VALUES ($repoId, $option, $value);";
            insert.Parameters.AddWithValue("$repoId", repositoryId);
            insert.Parameters.AddWithValue("$option", option);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Aliases (ordered) of the repositories that have no own value for <paramref name="option"/> and so inherit it.</summary>
    public IReadOnlyList<string> ListAliasesInheriting(string option)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alias FROM repositories
            WHERE id NOT IN (SELECT repo_id FROM repository_defaults WHERE option = $option)
            ORDER BY alias;
            """;
        command.Parameters.AddWithValue("$option", option);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public ResolvedDefaults ResolveRepositoryDefaults(long repositoryId) =>
        OptionDefaults.Resolve(GetGlobalDefaults(), GetRepositoryDefaults(repositoryId));

    private static List<(string Option, string Value)> ReadOptionRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }
}
