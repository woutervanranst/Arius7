using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests;

/// <summary>
/// End-to-end tests against a real Azure Storage account.
///
/// Gated by environment variables:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// Fails when the env vars are not set.
/// Each test creates and cleans up its own unique container.
///
/// Retains only the live Azure credential sanity check; representative coverage lives elsewhere.
/// </summary>
[ClassDataSource<AzureFixture>(Shared = SharedType.PerTestSession)]
internal class E2ETests(AzureFixture azure)
{
    [Test]
    public async Task E2E_Configuration_IsAvailable_WhenAzureBackendIsEnabled()
    {
        AzureFixture.AccountName.ShouldNotBeNullOrWhiteSpace();
        AzureFixture.AccountKey.ShouldNotBeNullOrWhiteSpace();

        // Create and immediately clean up a container to validate credentials work
        var (container, _, cleanup) = await azure.CreateTestContainerAsync();
        try
        {
            var exists = await container.ExistsAsync();
            exists.Value.ShouldBeTrue("Container should have been created");
        }
        finally { await cleanup(); }
    }
}
