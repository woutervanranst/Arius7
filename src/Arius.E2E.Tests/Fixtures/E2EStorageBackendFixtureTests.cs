namespace Arius.E2E.Tests.Fixtures;

public class E2EStorageBackendFixtureTests
{
    [Test]
    public async Task AzureFixture_CompatibilityType_ReportsArchiveCapability()
    {
        await using var backend = new AzureFixture();
        await backend.InitializeAsync();

        var context = await backend.CreateContextAsync();

        context.Capabilities.SupportsArchiveTier.ShouldBeTrue();
        await context.DisposeAsync();
    }
}
