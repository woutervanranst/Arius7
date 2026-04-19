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
    public async Task AzureFixture_CreateContext_PopulatesAzureBackendFields_WhenCredentialsAvailable()
    {
        if (!AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live backend context test");
            return;
        }

        await using var backend = new AzureFixture();
        await backend.InitializeAsync();

        var context = await backend.CreateContextAsync();

        context.BlobContainer.ShouldNotBeNull();
        context.AccountName.ShouldNotBeNullOrWhiteSpace();
        context.ContainerName.ShouldNotBeNullOrWhiteSpace();
        context.BlobContainerClient.ShouldNotBeNull();
        context.AzureBlobContainerService.ShouldNotBeNull();
        context.Capabilities.SupportsArchiveTier.ShouldBeTrue();

        context.AccountName.ShouldBe(context.BlobContainerClient.AccountName);
        context.ContainerName.ShouldBe(context.BlobContainerClient.Name);

        await context.DisposeAsync();
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
