using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.Features.Accounts;
using Arius.Api.Features.Repositories;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Repositories;

/// <summary>The <c>/repos</c> endpoints: create, list and update a repository. The passphrase is stored encrypted and
/// never returned, and an update discards what was cached for the old connection.</summary>
public class RepositoryEndpointsTests
{
    [Test]
    public async Task A_created_repository_reports_its_account_and_default_tier_and_its_passphrase_is_stored_encrypted()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var secrets = factory.Services.GetRequiredService<SecretProtector>();
        var accountId = db.InsertAccount("acct", secrets.Protect("key"));

        using var created = await client.PostAsJsonAsync("/api/repos",
            new CreateRepositoryRequest(accountId, "photos-container", "Photos", "pass-phrase", "/data/photos"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await created.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("pass-phrase");
        var repo = JsonSerializer.Deserialize<RepositoryDto>(body, JsonSerializerOptions.Web)!;
        await Assert.That(repo).IsEqualTo(new RepositoryDto(repo.Id, "Photos", "photos-container", accountId, "acct", "/data/photos",
            DefaultTier: "archive", Region: null, RegionIsDefault: false, LastArchiveStartedAt: null));

        await Assert.That(secrets.Unprotect(db.GetRepository(repo.Id)!.EncryptedPassphrase)).IsEqualTo("pass-phrase");
        await Assert.That((await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{accountId}"))!.Repositories).IsEqualTo(1);
    }

    [Test]
    public async Task Creating_a_repository_for_an_unknown_account_is_rejected()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/repos",
            new CreateRepositoryRequest(AccountId: 999, "container", "Alias", Passphrase: null, LocalPath: null));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Account 999 does not exist.");
        await Assert.That(factory.Services.GetRequiredService<AppDatabase>().ListRepositories()).IsEmpty();
    }

    [Test]
    public async Task The_list_reports_each_repository_with_its_cached_region_and_latest_archive_start()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();

        var archived = factory.SeedRepository(localPath: "/data/itest");
        db.SetRepositoryRegionHint(archived, "westeurope");
        db.InsertJob("archive-1", archived, "archive", "one-off", "completed");
        db.InsertJob("restore-1", archived, "restore", "one-off", "completed");   // later, but not an archive
        var archiveStartedAt = db.GetJob("archive-1")!.StartedAt;

        var accountId = db.InsertAccount("second-account", null);
        var fresh = db.InsertRepository("another", "another-container", accountId, localPath: null, encryptedPassphrase: null);

        var repos = await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repos");

        // Ordered by alias. The fresh repository has no cached region and the scripted Core cannot resolve one.
        await Assert.That(repos!.Count).IsEqualTo(2);
        await Assert.That(repos[0]).IsEqualTo(new RepositoryDto(fresh, "another", "another-container", accountId, "second-account", null,
            DefaultTier: "archive", Region: null, RegionIsDefault: false, LastArchiveStartedAt: null));
        await Assert.That(repos[1]).IsEqualTo(new RepositoryDto(archived, "itest", "itest-container", repos[1].AccountId, "fake-account", "/data/itest",
            DefaultTier: "archive", Region: "westeurope", RegionIsDefault: false, LastArchiveStartedAt: archiveStartedAt));
    }

    [Test]
    public async Task Updating_a_repository_changes_only_the_supplied_properties_and_drops_its_cached_region_and_statistics()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var secrets = factory.Services.GetRequiredService<SecretProtector>();
        var repoId = factory.SeedRepository(localPath: "/data/old");
        db.SetRepositoryRegionHint(repoId, "westeurope");
        db.UpsertCachedStatistics(repoId, version: "", full: false, fingerprint: "v1", payload: "{}");
        db.InsertJob("archive-1", repoId, "archive", "one-off", "completed");
        var archiveStartedAt = db.GetJob("archive-1")!.StartedAt;

        using var renamed = await client.PatchAsJsonAsync($"/api/repos/{repoId}", new UpdateRepositoryRequest("renamed", LocalPath: null, Passphrase: null));

        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var dto = await renamed.Content.ReadFromJsonAsync<RepositoryDto>();
        await Assert.That(dto!.Alias).IsEqualTo("renamed");
        await Assert.That(dto.LocalPath).IsEqualTo("/data/old");
        await Assert.That(dto.Region).IsNull();
        await Assert.That(dto.LastArchiveStartedAt).IsEqualTo(archiveStartedAt);
        await Assert.That(secrets.Unprotect(db.GetRepository(repoId)!.EncryptedPassphrase)).IsEqualTo("passphrase");
        await Assert.That(db.GetRepository(repoId)!.RegionHint).IsNull();
        await Assert.That(db.GetCachedStatistics(repoId, version: "", full: false)).IsNull();

        using var moved = await client.PatchAsJsonAsync($"/api/repos/{repoId}", new UpdateRepositoryRequest(Alias: null, "/data/new", "new-passphrase"));

        await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var movedDto = await moved.Content.ReadFromJsonAsync<RepositoryDto>();
        await Assert.That(movedDto!.Alias).IsEqualTo("renamed");
        await Assert.That(movedDto.LocalPath).IsEqualTo("/data/new");
        await Assert.That(secrets.Unprotect(db.GetRepository(repoId)!.EncryptedPassphrase)).IsEqualTo("new-passphrase");
    }

    [Test]
    public async Task An_unknown_repository_is_404_for_read_update_and_delete()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        using var get = await client.GetAsync("/api/repos/999");
        using var patch = await client.PatchAsJsonAsync("/api/repos/999", new UpdateRepositoryRequest("alias", null, null));
        using var delete = await client.DeleteAsync("/api/repos/999");

        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
