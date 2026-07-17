using System.Net;
using Arius.Api.Integration.Tests.Harness;

namespace Arius.Api.Integration.Tests;

public class HealthSmokeTests
{
    [Test]
    public async Task Health_endpoint_returns_ok()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
