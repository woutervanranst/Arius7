using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.Features.Accounts;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Accounts;

/// <summary>The <c>/accounts</c> endpoints: create, list, read, rotate the key and delete — the key is stored
/// encrypted and never returned.</summary>
public class AccountEndpointsTests
{
    [Test]
    public async Task A_created_account_is_listed_and_readable_and_its_key_is_stored_encrypted_and_never_returned()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("myaccount", "secret-key"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await created.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("secret-key");
        var account = JsonSerializer.Deserialize<AccountDto>(body, JsonSerializerOptions.Web)!;
        await Assert.That(account).IsEqualTo(new AccountDto(account.Id, "myaccount", Repositories: 0, HasKey: true));

        await Assert.That(await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}")).IsEqualTo(account);
        await Assert.That(await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts")).IsEquivalentTo([account]);

        var stored = factory.Services.GetRequiredService<AppDatabase>().GetAccount(account.Id)!.EncryptedAccountKey;
        await Assert.That(stored).IsNotEqualTo("secret-key");
        await Assert.That(factory.Services.GetRequiredService<SecretProtector>().Unprotect(stored)).IsEqualTo("secret-key");
    }

    [Test]
    public async Task An_account_created_without_a_key_reports_it_has_none()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("keyless", AccountKey: null));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var account = await created.Content.ReadFromJsonAsync<AccountDto>();
        await Assert.That(account!.HasKey).IsFalse();
    }

    [Test]
    public async Task Updating_an_account_rotates_its_key_and_a_null_key_leaves_it_unchanged()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var secrets = factory.Services.GetRequiredService<SecretProtector>();
        var accountId = db.GetRepository(factory.SeedRepository())!.AccountId;

        using var rotated = await client.PatchAsJsonAsync($"/api/accounts/{accountId}", new UpdateAccountRequest("rotated-key"));

        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await rotated.Content.ReadFromJsonAsync<AccountDto>())
            .IsEqualTo(new AccountDto(accountId, "fake-account", Repositories: 1, HasKey: true));
        await Assert.That(secrets.Unprotect(db.GetAccount(accountId)!.EncryptedAccountKey)).IsEqualTo("rotated-key");

        using var unchanged = await client.PatchAsJsonAsync($"/api/accounts/{accountId}", new UpdateAccountRequest(AccountKey: null));

        await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(secrets.Unprotect(db.GetAccount(accountId)!.EncryptedAccountKey)).IsEqualTo("rotated-key");
    }

    [Test]
    public async Task An_account_with_repositories_cannot_be_deleted_until_they_are_gone()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var repoId = factory.SeedRepository();
        var accountId = db.GetRepository(repoId)!.AccountId;

        using var refused = await client.DeleteAsync($"/api/accounts/{accountId}");

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Account still has repositories.");
        await Assert.That(db.GetAccount(accountId)).IsNotNull();

        using var repoDeleted = await client.DeleteAsync($"/api/repos/{repoId}");
        await Assert.That(repoDeleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var deleted = await client.DeleteAsync($"/api/accounts/{accountId}");

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var gone = await client.GetAsync($"/api/accounts/{accountId}");
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task An_unknown_account_is_404_for_read_update_and_delete()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var get = await client.GetAsync("/api/accounts/999");
        using var patch = await client.PatchAsJsonAsync("/api/accounts/999", new UpdateAccountRequest("key"));
        using var delete = await client.DeleteAsync("/api/accounts/999");

        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
