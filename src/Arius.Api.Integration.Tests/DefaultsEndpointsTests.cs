using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

/// <summary>
/// The defaults hierarchy over REST: global settings and per-repository properties, with the camelCase JSON
/// shape the Web client relies on.
/// </summary>
public class DefaultsEndpointsTests
{
    private static readonly string[] OptionNames = ["tier", "onDisk", "fastHash", "overwrite", "noPointers"];

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public async Task Global_defaults_start_empty_and_report_factory_values()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        factory.SeedRepository();

        var response = await client.GetAsync("/api/settings/defaults");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await ReadAsync(response);

        foreach (var option in OptionNames)
            await Assert.That(body.GetProperty("set").GetProperty(option).ValueKind).IsEqualTo(JsonValueKind.Null);

        var factoryValues = body.GetProperty("factory");
        await Assert.That(factoryValues.GetProperty("tier").GetString()).IsEqualTo("archive");
        await Assert.That(factoryValues.GetProperty("onDisk").GetString()).IsEqualTo("keep");
        await Assert.That(factoryValues.GetProperty("fastHash").GetBoolean()).IsFalse();
        await Assert.That(factoryValues.GetProperty("overwrite").GetBoolean()).IsFalse();
        await Assert.That(factoryValues.GetProperty("noPointers").GetBoolean()).IsFalse();

        var inheritedBy = body.GetProperty("inheritedBy");
        foreach (var option in OptionNames)
            await Assert.That(inheritedBy.GetProperty(option).EnumerateArray().Select(e => e.GetString()!).ToArray())
                .IsEquivalentTo(new[] { "itest" });
    }

    [Test]
    public async Task Global_change_reaches_an_inheriting_repository_with_settings_source()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        var put = await client.PutAsJsonAsync("/api/settings/defaults", new { onDisk = "keep-pointers" });
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadAsync(put)).GetProperty("set").GetProperty("onDisk").GetString()).IsEqualTo("keep-pointers");

        var body = await ReadAsync(await client.GetAsync($"/api/repos/{repoId}/defaults"));
        var inherited = body.GetProperty("inherited");
        await Assert.That(inherited.GetProperty("onDisk").GetProperty("value").GetString()).IsEqualTo("keep-pointers");
        await Assert.That(inherited.GetProperty("onDisk").GetProperty("source").GetString()).IsEqualTo("settings");
        await Assert.That(inherited.GetProperty("tier").GetProperty("source").GetString()).IsEqualTo("factory");
        await Assert.That(body.GetProperty("set").GetProperty("onDisk").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Repository_value_survives_a_global_change()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        var repoPut = await client.PutAsJsonAsync($"/api/repos/{repoId}/defaults", new { fastHash = false });
        await Assert.That(repoPut.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var globalPut = await client.PutAsJsonAsync("/api/settings/defaults", new { fastHash = true });
        await Assert.That(globalPut.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var repoBody = await ReadAsync(await client.GetAsync($"/api/repos/{repoId}/defaults"));
        await Assert.That(repoBody.GetProperty("set").GetProperty("fastHash").GetBoolean()).IsFalse();

        var globalBody = await ReadAsync(await client.GetAsync("/api/settings/defaults"));
        var fastHashInheritors = globalBody.GetProperty("inheritedBy").GetProperty("fastHash")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        await Assert.That(fastHashInheritors).DoesNotContain("itest");
    }

    [Test]
    public async Task Repository_dto_reports_the_resolved_tier()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        var put = await client.PutAsJsonAsync("/api/settings/defaults", new { tier = "cool" });
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var repo = await ReadAsync(await client.GetAsync($"/api/repos/{repoId}"));
        await Assert.That(repo.GetProperty("defaultTier").GetString()).IsEqualTo("cool");
    }

    [Test]
    public async Task Unknown_option_is_rejected_and_nothing_changes()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        factory.SeedRepository();

        var ok = await client.PutAsJsonAsync("/api/settings/defaults", new { tier = "hot" });
        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var rejected = await client.PutAsync("/api/settings/defaults", Json("""{"fasthash2":true}"""));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var body = await ReadAsync(await client.GetAsync("/api/settings/defaults"));
        await Assert.That(body.GetProperty("set").GetProperty("tier").GetString()).IsEqualTo("hot");
    }

    [Test]
    public async Task Invalid_tier_is_rejected()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        var global = await client.PutAsJsonAsync("/api/settings/defaults", new { tier = "premium" });
        await Assert.That(global.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var repository = await client.PutAsJsonAsync($"/api/repos/{repoId}/defaults", new { tier = "premium" });
        await Assert.That(repository.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Unknown_repository_is_404()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var get = await client.GetAsync("/api/repos/999/defaults");
        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        var put = await client.PutAsJsonAsync("/api/repos/999/defaults", new { tier = "hot" });
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Inherited_by_lists_every_repository_without_its_own_value()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var first = factory.SeedRepository();

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var accountId = db.InsertAccount("second-account", null);
        db.InsertRepository("another", "another-container", accountId, localPath: null, encryptedPassphrase: null);

        await client.PutAsJsonAsync($"/api/repos/{first}/defaults", new { overwrite = true });

        var body = await ReadAsync(await client.GetAsync("/api/settings/defaults"));
        var overwriteInheritors = body.GetProperty("inheritedBy").GetProperty("overwrite")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        await Assert.That(overwriteInheritors).IsEquivalentTo(new[] { "another" });
    }
}
