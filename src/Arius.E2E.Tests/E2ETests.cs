using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Paths;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests;

/// <summary>
/// End-to-end tests against a real Azure Storage account.
///
/// Gated by environment variables:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// Skips live-only coverage when the env vars are not set.
/// Each test creates and cleans up its own unique container.
///
/// Retains the live Azure credential sanity check plus unique hot-tier pointer and large-file probes;
/// representative coverage lives elsewhere.
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

    [Test]
    public async Task E2E_HotTier_Restore_CreatesPointerFiles_ByDefault()
    {
        if (!AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live hot-tier restore sanity test");
            return;
        }

        var (container, service, cleanup) = await azure.CreateTestContainerAsync();
        var fixture = await E2EFixture.CreateAsync(container, service, BlobTier.Hot);
        try
        {
            var content = new byte[2048];
            Random.Shared.NextBytes(content);
            fixture.WriteFile(PathOf("hot.bin"), content);

            var archiveResult = await fixture.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            var restoreResult = await fixture.RestoreAsync();
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            (fixture.RestoreRoot / PathOf("hot.bin.pointer.arius")).ExistsFile.ShouldBeTrue();
            fixture.ReadRestored(PathOf("hot.bin")).ShouldBe(content);
        }
        finally
        {
            await fixture.DisposeAsync();
            await cleanup();
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task E2E_LargeFile_Streaming_RemainsCovered(CancellationToken cancellationToken)
    {
        if (!AzureFixture.IsAvailable)
        {
            Skip.Unless(false, "Azure credentials not available — skipping live large-file sanity test");
            return;
        }

        var (container, service, cleanup) = await azure.CreateTestContainerAsync(cancellationToken);
        var fixture = await E2EFixture.CreateAsync(container, service, BlobTier.Hot, ct: cancellationToken);
        try
        {
            var content = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(content);
            fixture.WriteFile(PathOf("large.bin"), content);

            var archiveResult = await fixture.ArchiveAsync(cancellationToken);
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
            archiveResult.FilesUploaded.ShouldBe(1);

            var restoreResult = await fixture.RestoreAsync(cancellationToken);
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            fixture.ReadRestored(PathOf("large.bin")).ShouldBe(content);
        }
        finally
        {
            await fixture.DisposeAsync();
            await cleanup();
        }
    }
}
