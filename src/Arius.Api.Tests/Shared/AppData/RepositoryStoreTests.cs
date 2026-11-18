using Arius.Api.Shared.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class RepositoryStoreTests
{
    [Test]
    public async Task Inserted_repository_is_returned_by_id_with_all_its_values()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();

        var id = temp.Database.InsertRepository("music", "music-container", accountId, "/data/music", "cipher-pass");

        var repository = temp.Database.GetRepository(id)!;
        repository.Id.ShouldBe(id);
        repository.Alias.ShouldBe("music");
        repository.Container.ShouldBe("music-container");
        repository.AccountId.ShouldBe(accountId);
        repository.LocalPath.ShouldBe("/data/music");
        repository.EncryptedPassphrase.ShouldBe("cipher-pass");
        repository.RegionHint.ShouldBeNull();
    }

    [Test]
    public async Task Repository_without_a_local_path_or_passphrase_reads_back_with_nulls()
    {
        using var temp = new TempAppDatabase();

        var id = temp.AddRepository(temp.AddAccount());

        var repository = temp.Database.GetRepository(id)!;
        repository.LocalPath.ShouldBeNull();
        repository.EncryptedPassphrase.ShouldBeNull();
    }

    [Test]
    public async Task GetRepository_of_an_unknown_id_is_null()
    {
        using var temp = new TempAppDatabase();

        temp.Database.GetRepository(999).ShouldBeNull();
    }

    [Test]
    public async Task ListRepositories_is_ordered_by_alias_not_insertion()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        temp.AddRepository(accountId, "zeta", "c1");
        temp.AddRepository(accountId, "alpha", "c2");
        temp.AddRepository(accountId, "mid", "c3");

        temp.Database.ListRepositories().Select(r => r.Alias).ShouldBe(["alpha", "mid", "zeta"]);
    }

    [Test]
    public async Task The_same_container_cannot_be_registered_twice_for_one_account()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        temp.AddRepository(accountId, "first", "shared-container");

        Should.Throw<SqliteException>(() => temp.AddRepository(accountId, "second", "shared-container"));

        temp.Database.ListRepositories().Count.ShouldBe(1);
    }

    [Test]
    public async Task The_same_container_name_is_allowed_under_different_accounts()
    {
        using var temp = new TempAppDatabase();
        var accountA = temp.AddAccount("a");
        var accountB = temp.AddAccount("b");

        temp.AddRepository(accountA, "first", "shared-container");
        temp.AddRepository(accountB, "second", "shared-container");

        temp.Database.ListRepositories().Count.ShouldBe(2);
    }

    [Test]
    public async Task UpdateRepository_replaces_the_supplied_values()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertRepository("old", "container", temp.AddAccount(), "/old", "old-pass");

        temp.Database.UpdateRepository(id, "new", "/new", "new-pass");

        var repository = temp.Database.GetRepository(id)!;
        repository.Alias.ShouldBe("new");
        repository.LocalPath.ShouldBe("/new");
        repository.EncryptedPassphrase.ShouldBe("new-pass");
    }

    [Test]
    public async Task UpdateRepository_leaves_columns_unchanged_when_their_argument_is_null()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertRepository("alias", "container", temp.AddAccount(), "/path", "pass");

        temp.Database.UpdateRepository(id, alias: null, localPath: null, encryptedPassphrase: null);

        var repository = temp.Database.GetRepository(id)!;
        repository.Alias.ShouldBe("alias");
        repository.LocalPath.ShouldBe("/path");
        repository.EncryptedPassphrase.ShouldBe("pass");
    }

    [Test]
    public async Task UpdateRepository_updates_one_column_without_touching_the_others()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertRepository("alias", "container", temp.AddAccount(), "/path", "pass");

        temp.Database.UpdateRepository(id, alias: "renamed", localPath: null, encryptedPassphrase: null);

        var repository = temp.Database.GetRepository(id)!;
        repository.Alias.ShouldBe("renamed");
        repository.LocalPath.ShouldBe("/path");
        repository.EncryptedPassphrase.ShouldBe("pass");
        repository.Container.ShouldBe("container");
    }

    [Test]
    public async Task SetRepositoryRegionHint_memoizes_a_region_and_null_invalidates_it()
    {
        using var temp = new TempAppDatabase();
        var id = temp.AddRepository(temp.AddAccount());

        temp.Database.SetRepositoryRegionHint(id, "westeurope");
        temp.Database.GetRepository(id)!.RegionHint.ShouldBe("westeurope");

        temp.Database.SetRepositoryRegionHint(id, null);
        temp.Database.GetRepository(id)!.RegionHint.ShouldBeNull();
    }

    [Test]
    public async Task DeleteRepository_removes_it_together_with_its_jobs_and_leaves_other_repositories_untouched()
    {
        using var temp = new TempAppDatabase();
        var accountId = temp.AddAccount();
        var doomed = temp.AddRepository(accountId, "doomed", "c1");
        var kept   = temp.AddRepository(accountId, "kept", "c2");
        temp.Database.InsertJob("doomed-job", doomed, "archive", "one-off", "running");
        temp.Database.InsertJob("kept-job", kept, "archive", "one-off", "running");

        temp.Database.DeleteRepository(doomed);

        temp.Database.GetRepository(doomed).ShouldBeNull();
        temp.Database.GetJob("doomed-job").ShouldBeNull();
        temp.Database.GetRepository(kept).ShouldNotBeNull();
        temp.Database.GetJob("kept-job").ShouldNotBeNull();
    }

    [Test]
    public async Task DeleteRepository_of_an_unknown_id_is_a_no_op()
    {
        using var temp = new TempAppDatabase();
        var id = temp.AddRepository(temp.AddAccount());

        Should.NotThrow(() => temp.Database.DeleteRepository(id + 100));

        temp.Database.ListRepositories().Count.ShouldBe(1);
    }
}
