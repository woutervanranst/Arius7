namespace Arius.E2E.Tests.Fixtures;

public class E2EStorageBackendFixtureTests
{
    [Test]
    public void AzureFixture_CompatibilityType_ReportsAzureBackendShape()
    {
        var backend = new AzureFixture();

        backend.Name.ShouldBe("Azure");
        backend.Capabilities.SupportsArchiveTier.ShouldBeTrue();
        backend.Capabilities.SupportsRehydrationPlanning.ShouldBeTrue();
    }

    [Test]
    public async Task Azurite_Backend_Context_ReportsLimitedCapabilities()
    {
        await using var backend = new AzuriteE2EBackendFixture();
        await backend.InitializeAsync();

        var context = await backend.CreateContextAsync();

        context.Capabilities.SupportsArchiveTier.ShouldBeFalse();
        context.Capabilities.SupportsRehydrationPlanning.ShouldBeFalse();
        await context.DisposeAsync();
    }

    [Test]
    public async Task Azurite_Backend_Context_Dispose_IgnoresCreationCancellation()
    {
        await using var backend = new AzuriteE2EBackendFixture();
        await backend.InitializeAsync();

        using var cancellationTokenSource = new CancellationTokenSource();
        var context = await backend.CreateContextAsync(cancellationTokenSource.Token);

        cancellationTokenSource.Cancel();

        await context.DisposeAsync();
    }
}
