namespace Arius.E2E.Tests.Fixtures;

public class E2EStorageBackendFixtureTests
{
    [Test]
    public async Task Azure_Backend_Context_ReportsArchiveCapability()
    {
        await using var backend = new AzureE2EBackendFixture();
        await backend.InitializeAsync();

        var context = await backend.CreateContextAsync();

        context.Capabilities.SupportsArchiveTier.ShouldBeTrue();
        await context.DisposeAsync();
    }
}
