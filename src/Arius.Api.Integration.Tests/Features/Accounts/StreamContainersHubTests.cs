using Arius.Api.FakeTestHost;
using Arius.Api.Integration.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;

namespace Arius.Api.Integration.Tests.Features.Accounts;

public class StreamContainersHubTests
{
    [Test]
    public async Task StreamContainers_lists_the_account_containers_through_Core()
    {
        await using var factory = new AriusApiFactory();

        var handler = factory.Server.CreateHandler();
        await using var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/arius", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        await conn.StartAsync();

        // accountId 0 = a not-yet-saved account: explicit name + key, no repository involved.
        var names = new List<string>();
        await foreach (var name in conn.StreamAsync<string>("StreamContainers", 0L, "new-account", "new-key"))
            names.Add(name);

        names.ShouldBe(ScriptedContainerNamesHandler.ContainerNames);
    }
}
