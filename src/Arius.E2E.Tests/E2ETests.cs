using Arius.Core.Shared.Storage;
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
/// Covers tasks 16.1 – 16.5.
/// </summary>
[ClassDataSource<AzureFixture>(Shared = SharedType.PerTestSession)]
internal class E2ETests(AzureFixture azure)
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a pipeline fixture backed by the real Azure container.
    /// The caller is responsible for calling cleanup when done.
    /// </summary>
    private async Task<(E2EFixture Fixture, Func<Task> Cleanup)> CreateFixtureAsync(
        BlobTier tier,
        string?  passphrase = null,
        CancellationToken ct = default)
    {
        var (container, svc, cleanup) = await azure.CreateTestContainerAsync(ct);
        var fix = await E2EFixture.CreateAsync(svc, container.AccountName, container.Name, tier, passphrase, ct);
        return (fix, async () =>
        {
            await fix.DisposeAsync();
            await cleanup();
        });
    }

    // ── 16.1: Configuration is set up ─────────────────────────────────────────

    [Test]
    public async Task E2E_Configuration_IsAvailable_WhenEnvVarsSet()
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

    // ── 16.2: Archive to Hot tier → restore → verify content ─────────────────

    [Test]
    public async Task E2E_HotTier_Archive_Restore_ByteIdentical()
    {
        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Hot);
        try
        {
            var content = new byte[1024]; Random.Shared.NextBytes(content);
            fix.WriteFile("hot.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
            archiveResult.FilesUploaded.ShouldBe(1);

            var restoreResult = await fix.RestoreAsync();
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            fix.ReadRestored("hot.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }

    // ── 16.3: Archive to Cool tier → restore → verify content ────────────────

    [Test]
    public async Task E2E_CoolTier_Archive_Restore_ByteIdentical()
    {
        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Cool);
        try
        {
            var content = new byte[512]; Random.Shared.NextBytes(content);
            fix.WriteFile("cool.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            var restoreResult = await fix.RestoreAsync();
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

            fix.ReadRestored("cool.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }

    // ── 16.4: Archive to Archive tier → verify blob tier is set ──────────────

    [Test]
    public async Task E2E_ArchiveTier_BlobTierIsSet()
    {
        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Archive);
        try
        {
            var content = new byte[256]; Random.Shared.NextBytes(content);
            fix.WriteFile("archival.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            // Verify at least one chunk blob has Archive tier
            var foundArchiveTierBlob = false;
            await foreach (var blobName in fix.BlobContainer.ListAsync(BlobPaths.Chunks))
            {
                var meta = await fix.BlobContainer.GetMetadataAsync(blobName);
                if (meta.Tier == BlobTier.Archive)
                {
                    foundArchiveTierBlob = true;
                    break;
                }
            }
            foundArchiveTierBlob.ShouldBeTrue("Expected at least one chunk blob with Archive tier");
        }
        finally { await cleanup(); }
    }

    // ── 16.5: Large file (100 MB+) upload/download streaming ──────────────────

    [Test]
    [Timeout(300_000)] // 5 minute timeout for large file upload
    public async Task E2E_LargeFile_100MB_Streaming(CancellationToken ct)
    {
        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Hot, ct: ct);
        try
        {
            // 100 MB file → well above threshold → large pipeline
            var content = new byte[100 * 1024 * 1024];
            Random.Shared.NextBytes(content);
            fix.WriteFile("large100mb.bin", content);

            var archiveResult = await fix.ArchiveAsync(ct);
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
            archiveResult.FilesUploaded.ShouldBe(1);

            var restoreResult = await fix.RestoreAsync(ct);
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            fix.ReadRestored("large100mb.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }
}
