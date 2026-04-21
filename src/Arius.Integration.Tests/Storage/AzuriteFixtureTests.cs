using Arius.Tests.Shared.Storage;
using DotNet.Testcontainers.Builders;
using TUnit.Core;

namespace Arius.Integration.Tests.Storage;

public class AzuriteFixtureTests
{
    [Test]
    public async Task Initialize_DoesNotThrow_WhenDockerIsUnavailable()
    {
        await using var fixture = CreateUnavailableFixture();

        await fixture.InitializeAsync();
    }

    [Test]
    public async Task CreateTestService_Skips_WhenDockerIsUnavailable()
    {
        await using var fixture = CreateUnavailableFixture();
        await fixture.InitializeAsync();

        var exception = await Should.ThrowAsync<Exception>(() => fixture.CreateTestServiceAsync());

        exception.Message.ShouldContain("Docker is unavailable for Azurite-backed tests");
    }

    [Test]
    public async Task ConnectionString_Skips_WhenDockerIsUnavailable()
    {
        await using var fixture = CreateUnavailableFixture();
        await fixture.InitializeAsync();

        Should.Throw<Exception>(() => _ = fixture.ConnectionString)
            .Message.ShouldContain("Docker is unavailable for Azurite-backed tests");
    }

    static AzuriteFixture CreateUnavailableFixture()
        => new(() => Task.FromException<Testcontainers.Azurite.AzuriteContainer>(new DockerUnavailableException("Docker unavailable for test")));
}
