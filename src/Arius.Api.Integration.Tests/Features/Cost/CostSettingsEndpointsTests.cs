using System.Net.Http.Json;
using System.Net;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;

namespace Arius.Api.Integration.Tests.Features.Cost;

public class CostSettingsEndpointsTests
{
    [Test]
    public async Task Cost_settings_default_to_one_cent_save_and_reject_amounts_outside_0_to_1000_or_finer_than_a_cent()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        await Assert.That(await client.GetFromJsonAsync<CostSettings>("/api/settings/cost")).IsEqualTo(new CostSettings(0.01m));

        using var saved = await client.PutAsJsonAsync("/api/settings/cost", new CostSettings(0.5m));
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await saved.Content.ReadFromJsonAsync<CostSettings>()).IsEqualTo(new CostSettings(0.5m));
        await Assert.That(await client.GetFromJsonAsync<CostSettings>("/api/settings/cost")).IsEqualTo(new CostSettings(0.5m));

        foreach (var amount in new[] { -1m, 1000.01m, 0.001m })
        {
            using var rejected = await client.PutAsJsonAsync("/api/settings/cost", new CostSettings(amount));
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("Enter an amount from 0 to 1000 EUR, to the cent.");
        }
        await Assert.That(await client.GetFromJsonAsync<CostSettings>("/api/settings/cost")).IsEqualTo(new CostSettings(0.5m));
    }
}
