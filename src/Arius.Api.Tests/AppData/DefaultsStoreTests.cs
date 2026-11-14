using Arius.Api.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.AppData;

public sealed class DefaultsStoreTests
{
    private static (AppDatabase Database, string Path, long AccountId) NewDatabase()
    {
        var path      = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database  = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        return (database, path, accountId);
    }

    private static long AddRepository(AppDatabase database, long accountId, string alias) =>
        database.InsertRepository(alias, $"container-{alias}", accountId, localPath: null, "archive", encryptedPassphrase: null);

    [Test]
    public async Task Fresh_database_has_no_defaults_set()
    {
        var (database, _, accountId) = NewDatabase();
        var repositoryId = AddRepository(database, accountId, "music");

        database.GetGlobalDefaults().ShouldBe(DefaultValues.None);
        database.GetRepositoryDefaults(repositoryId).ShouldBe(DefaultValues.None);
    }

    [Test]
    public async Task Replace_global_round_trips_and_replaces_previous_set()
    {
        var (database, _, _) = NewDatabase();

        database.ReplaceGlobalDefaults(DefaultValues.None with { Tier = "cool", FastHash = false });
        database.GetGlobalDefaults().ShouldBe(DefaultValues.None with { Tier = "cool", FastHash = false });

        database.ReplaceGlobalDefaults(DefaultValues.None with { OnDisk = "replace" });
        var read = database.GetGlobalDefaults();
        read.ShouldBe(DefaultValues.None with { OnDisk = "replace" });
        read.Tier.ShouldBeNull();
    }

    [Test]
    public async Task Replace_repository_round_trips_explicit_false()
    {
        var (database, _, accountId) = NewDatabase();
        var repositoryId = AddRepository(database, accountId, "music");

        database.ReplaceRepositoryDefaults(repositoryId, DefaultValues.None with { Overwrite = false });

        database.GetRepositoryDefaults(repositoryId).Overwrite.ShouldBe(false);
    }

    [Test]
    public async Task Resolve_repository_defaults_uses_both_levels()
    {
        var (database, _, accountId) = NewDatabase();
        var repositoryId = AddRepository(database, accountId, "music");
        database.ReplaceGlobalDefaults(DefaultValues.None with { OnDisk = "keep-pointers" });
        database.ReplaceRepositoryDefaults(repositoryId, DefaultValues.None with { FastHash = true });

        var resolved = database.ResolveRepositoryDefaults(repositoryId);

        resolved.OnDisk.ShouldBe(new ResolvedValue<string>("keep-pointers", "settings"));
        resolved.FastHash.ShouldBe(new ResolvedValue<bool>(true, "repository"));
        resolved.Tier.ShouldBe(new ResolvedValue<string>("archive", "factory"));
    }

    [Test]
    public async Task Aliases_inheriting_excludes_repositories_with_their_own_value()
    {
        var (database, _, accountId) = NewDatabase();
        AddRepository(database, accountId, "music");
        var work = AddRepository(database, accountId, "work");
        database.ReplaceRepositoryDefaults(work, DefaultValues.None with { OnDisk = "replace" });

        database.ListAliasesInheriting("onDisk").ShouldBe(["music"]);
        database.ListAliasesInheriting("tier").ShouldBe(["music", "work"]);
    }

    [Test]
    public async Task Deleting_a_repository_with_defaults_removes_them()
    {
        var (database, path, accountId) = NewDatabase();
        var repositoryId = AddRepository(database, accountId, "music");
        database.ReplaceRepositoryDefaults(repositoryId, DefaultValues.None with { Tier = "cold", FastHash = true });

        Should.NotThrow(() => database.DeleteRepository(repositoryId));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repository_defaults WHERE repo_id = $id;";
        command.Parameters.AddWithValue("$id", repositoryId);
        Convert.ToInt64(command.ExecuteScalar()).ShouldBe(0);
    }
}
