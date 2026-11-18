using Arius.Api.Shared.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class AccountStoreTests
{
    [Test]
    public async Task Inserted_account_is_returned_by_id_with_its_encrypted_key_and_a_creation_time()
    {
        using var temp = new TempAppDatabase();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var id = temp.Database.InsertAccount("storage1", "cipher-text");

        var account = temp.Database.GetAccount(id)!;
        account.Id.ShouldBe(id);
        account.Name.ShouldBe("storage1");
        account.EncryptedAccountKey.ShouldBe("cipher-text");
        account.CreatedAt.ShouldBeGreaterThan(before);
        account.CreatedAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Test]
    public async Task Account_without_a_key_reads_back_with_a_null_key()
    {
        using var temp = new TempAppDatabase();

        var id = temp.Database.InsertAccount("keyless", encryptedAccountKey: null);

        temp.Database.GetAccount(id)!.EncryptedAccountKey.ShouldBeNull();
    }

    [Test]
    public async Task GetAccount_of_an_unknown_id_is_null()
    {
        using var temp = new TempAppDatabase();

        temp.Database.GetAccount(12345).ShouldBeNull();
    }

    [Test]
    public async Task Account_names_are_unique()
    {
        using var temp = new TempAppDatabase();
        temp.Database.InsertAccount("dup", null);

        Should.Throw<SqliteException>(() => temp.Database.InsertAccount("dup", "other-key"));

        temp.Database.ListAccounts().Count.ShouldBe(1);
    }

    [Test]
    public async Task ListAccounts_is_ordered_by_name_not_insertion()
    {
        using var temp = new TempAppDatabase();
        temp.Database.InsertAccount("charlie", null);
        temp.Database.InsertAccount("alpha", null);
        temp.Database.InsertAccount("bravo", null);

        temp.Database.ListAccounts().Select(a => a.Name).ShouldBe(["alpha", "bravo", "charlie"]);
    }

    [Test]
    public async Task ListAccounts_on_an_empty_database_is_empty()
    {
        using var temp = new TempAppDatabase();

        temp.Database.ListAccounts().ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateAccount_replaces_the_stored_key()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertAccount("acc", "old-key");

        temp.Database.UpdateAccount(id, "new-key");

        temp.Database.GetAccount(id)!.EncryptedAccountKey.ShouldBe("new-key");
    }

    [Test]
    public async Task UpdateAccount_with_a_null_key_leaves_the_stored_key_unchanged()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertAccount("acc", "kept-key");

        temp.Database.UpdateAccount(id, null);

        temp.Database.GetAccount(id)!.EncryptedAccountKey.ShouldBe("kept-key");
    }

    [Test]
    public async Task UpdateAccount_changes_only_the_named_account()
    {
        using var temp = new TempAppDatabase();
        var target = temp.Database.InsertAccount("target", "k1");
        var other  = temp.Database.InsertAccount("other", "k2");

        temp.Database.UpdateAccount(target, "k1-new");

        temp.Database.GetAccount(other)!.EncryptedAccountKey.ShouldBe("k2");
    }

    [Test]
    public async Task DeleteAccount_removes_only_that_account()
    {
        using var temp = new TempAppDatabase();
        var doomed = temp.Database.InsertAccount("doomed", null);
        var kept   = temp.Database.InsertAccount("kept", null);

        temp.Database.DeleteAccount(doomed);

        temp.Database.GetAccount(doomed).ShouldBeNull();
        temp.Database.ListAccounts().Select(a => a.Id).ShouldBe([kept]);
    }

    [Test]
    public async Task DeleteAccount_of_an_unknown_id_is_a_no_op()
    {
        using var temp = new TempAppDatabase();
        var id = temp.Database.InsertAccount("acc", null);

        Should.NotThrow(() => temp.Database.DeleteAccount(id + 100));

        temp.Database.ListAccounts().Count.ShouldBe(1);
    }

    [Test]
    public async Task Repositories_are_counted_and_listed_per_account()
    {
        using var temp = new TempAppDatabase();
        var accountA = temp.Database.InsertAccount("a", null);
        var accountB = temp.Database.InsertAccount("b", null);
        var emptyAccount = temp.Database.InsertAccount("empty", null);
        var a1 = temp.AddRepository(accountA, "a1", "c1");
        var a2 = temp.AddRepository(accountA, "a2", "c2");
        var b1 = temp.AddRepository(accountB, "b1", "c1");

        temp.Database.CountRepositoriesForAccount(accountA).ShouldBe(2);
        temp.Database.CountRepositoriesForAccount(accountB).ShouldBe(1);
        temp.Database.CountRepositoriesForAccount(emptyAccount).ShouldBe(0);
        temp.Database.ListRepositoryIdsForAccount(accountA).OrderBy(i => i).ShouldBe([a1, a2]);
        temp.Database.ListRepositoryIdsForAccount(accountB).ShouldBe([b1]);
        temp.Database.ListRepositoryIdsForAccount(emptyAccount).ShouldBeEmpty();
    }
}
