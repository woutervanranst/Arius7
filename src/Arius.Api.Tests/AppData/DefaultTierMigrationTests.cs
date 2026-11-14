using Arius.Api.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.AppData;

public class DefaultTierMigrationTests
{
    [Test]
    public async Task Existing_tiers_migrate_as_repository_level_values()
    {
        var path = SeedOldSchema(("cool", 1), ("Archive", 2));

        var database = new AppDatabase(path);

        database.GetRepositoryDefaults(1).Tier.ShouldBe("cool");
        database.GetRepositoryDefaults(2).Tier.ShouldBe("archive");
        database.ResolveRepositoryDefaults(1).Tier.Source.ShouldBe("repository");
        database.ResolveRepositoryDefaults(2).Tier.Source.ShouldBe("repository");
    }

    [Test]
    public async Task Blank_or_unknown_tiers_are_not_migrated()
    {
        var path = SeedOldSchema(("", 1), ("premium", 2));

        var database = new AppDatabase(path);

        database.GetRepositoryDefaults(1).Tier.ShouldBeNull();
        database.GetRepositoryDefaults(2).Tier.ShouldBeNull();
        database.ResolveRepositoryDefaults(1).Tier.Source.ShouldBe("factory");
    }

    [Test]
    public async Task Migration_drops_the_column_and_is_idempotent()
    {
        var path = SeedOldSchema(("cold", 1));

        _ = new AppDatabase(path);
        HasDefaultTierColumn(path).ShouldBeFalse();

        var second = new AppDatabase(path);

        second.GetRepositoryDefaults(1).Tier.ShouldBe("cold");
    }

    [Test]
    public async Task Fresh_database_has_no_default_tier_column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arius-api-tests-tier-{Guid.NewGuid():N}.db");

        _ = new AppDatabase(path);

        HasDefaultTierColumn(path).ShouldBeFalse();
    }

    private static string SeedOldSchema(params (string Tier, int Id)[] repositories)
    {
        var path = Path.Combine(Path.GetTempPath(), $"arius-api-tests-tier-{Guid.NewGuid():N}.db");
        using (var seed = new SqliteConnection(ConnectionString(path)))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE storage_accounts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, account_key TEXT, created_at TEXT NOT NULL);
                CREATE TABLE repositories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, alias TEXT NOT NULL, container TEXT NOT NULL,
                    account_id INTEGER NOT NULL REFERENCES storage_accounts(id), local_path TEXT,
                    default_tier TEXT NOT NULL DEFAULT 'archive', passphrase TEXT, created_at TEXT NOT NULL,
                    UNIQUE(account_id, container));
                INSERT INTO storage_accounts(id, name, created_at) VALUES (1, 'acc', '2024-01-01T00:00:00.0000000+00:00');
                """;
            cmd.ExecuteNonQuery();

            foreach (var (tier, id) in repositories)
            {
                using var insert = seed.CreateCommand();
                insert.CommandText = """
                    INSERT INTO repositories(id, alias, container, account_id, default_tier, created_at)
                    VALUES ($id, $alias, $container, 1, $tier, '2024-01-01T00:00:00.0000000+00:00');
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$alias", $"alias-{id}");
                insert.Parameters.AddWithValue("$container", $"container-{id}");
                insert.Parameters.AddWithValue("$tier", tier);
                insert.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(path))); // release the seed file handle before reopening
        return path;
    }

    private static bool HasDefaultTierColumn(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('repositories') WHERE name = 'default_tier';";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
}
