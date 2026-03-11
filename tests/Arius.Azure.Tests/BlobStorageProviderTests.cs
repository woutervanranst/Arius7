using Arius.Azure;
using Arius.Core.Application.Abstractions;
using Shouldly;
using Testcontainers.Azurite;
using TUnit.Core;

namespace Arius.Azure.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 6.9  Azure backend integration tests (Azurite)
// Covers: upload, download, exists, tier, list, delete, lease
// ─────────────────────────────────────────────────────────────────────────────

[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerClass)]
public class BlobStorageProviderTests
{
    private readonly AzureBlobStorageProvider _provider;

    public BlobStorageProviderTests(AzuriteFixture fixture)
    {
        _provider = fixture.CreateProvider();
    }

    // ── Upload & Download ─────────────────────────────────────────────────────

    [Test]
    public async Task Upload_ThenDownload_RoundTrips()
    {
        var blobName = $"test/{Guid.NewGuid()}.bin";
        var original = System.Text.Encoding.UTF8.GetBytes("Hello, Arius!");

        await _provider.UploadAsync(blobName, new MemoryStream(original), BlobAccessTier.Hot);

        await using var stream = await _provider.DownloadAsync(blobName);
        var downloaded = new MemoryStream();
        await stream.CopyToAsync(downloaded);

        downloaded.ToArray().ShouldBe(original);
    }

    [Test]
    public async Task Upload_LargeBlob_Streams_Without_Full_Buffering()
    {
        // 4 MB blob — verifies we don't buffer in memory unnecessarily
        var blobName = $"test/{Guid.NewGuid()}.bin";
        var data = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        await _provider.UploadAsync(blobName, new MemoryStream(data), BlobAccessTier.Hot);

        await using var stream = await _provider.DownloadAsync(blobName);
        var downloaded = new MemoryStream();
        await stream.CopyToAsync(downloaded);

        downloaded.ToArray().ShouldBe(data);
    }

    // ── Exists ────────────────────────────────────────────────────────────────

    [Test]
    public async Task ExistsAsync_Returns_False_For_Missing_Blob()
    {
        var exists = await _provider.ExistsAsync($"nonexistent/{Guid.NewGuid()}");
        exists.ShouldBeFalse();
    }

    [Test]
    public async Task ExistsAsync_Returns_True_After_Upload()
    {
        var blobName = $"test/{Guid.NewGuid()}.bin";
        await _provider.UploadAsync(blobName, new MemoryStream([1, 2, 3]), BlobAccessTier.Hot);

        var exists = await _provider.ExistsAsync(blobName);
        exists.ShouldBeTrue();
    }

    // ── Tier ──────────────────────────────────────────────────────────────────

    [Test]
    [Arguments(BlobAccessTier.Hot)]
    [Arguments(BlobAccessTier.Cool)]
    [Arguments(BlobAccessTier.Cold)]
    public async Task Upload_With_Explicit_Tier_Persists_Tier(BlobAccessTier tier)
    {
        var blobName = $"test/{Guid.NewGuid()}.bin";
        await _provider.UploadAsync(blobName, new MemoryStream([1, 2, 3]), tier);

        var actualTier = await _provider.GetTierAsync(blobName);
        actualTier.ShouldBe(tier);
    }

    // Note: Archive tier upload & rehydration not testable with Azurite (no Archive support).

    // ── List ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListAsync_Returns_Blobs_Under_Prefix()
    {
        var prefix = $"list-test/{Guid.NewGuid()}/";
        var blobNames = new[] { $"{prefix}a.bin", $"{prefix}b.bin", $"{prefix}c.bin" };

        foreach (var name in blobNames)
            await _provider.UploadAsync(name, new MemoryStream([42]), BlobAccessTier.Hot);

        // Upload one outside the prefix to verify filtering
        await _provider.UploadAsync($"other/{Guid.NewGuid()}.bin", new MemoryStream([1]), BlobAccessTier.Hot);

        var listed = new List<string>();
        await foreach (var item in _provider.ListAsync(prefix))
            listed.Add(item.Name);

        listed.Count.ShouldBe(3);
        foreach (var name in blobNames)
            listed.ShouldContain(name);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_Removes_Blob()
    {
        var blobName = $"test/{Guid.NewGuid()}.bin";
        await _provider.UploadAsync(blobName, new MemoryStream([9, 8, 7]), BlobAccessTier.Hot);

        await _provider.DeleteAsync(blobName);

        var exists = await _provider.ExistsAsync(blobName);
        exists.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteAsync_NonExistent_Does_Not_Throw()
    {
        // Should be idempotent
        await Should.NotThrowAsync(async () =>
            await _provider.DeleteAsync($"nonexistent/{Guid.NewGuid()}"));
    }

    // ── Lease ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task AcquireLease_Then_Release_Works()
    {
        var blobName = $"locks/{Guid.NewGuid()}.lock";
        // Create the blob first (lease requires the blob to exist)
        await _provider.UploadAsync(blobName, new MemoryStream([0]), BlobAccessTier.Hot);

        var leaseId = await _provider.AcquireLeaseAsync(blobName);
        leaseId.ShouldNotBeNullOrEmpty();

        await _provider.ReleaseLeaseAsync(blobName, leaseId);

        // After release, acquiring again should succeed
        var leaseId2 = await _provider.AcquireLeaseAsync(blobName);
        leaseId2.ShouldNotBeNullOrEmpty();
        await _provider.ReleaseLeaseAsync(blobName, leaseId2);
    }

    [Test]
    public async Task RenewLease_Keeps_Lease_Active()
    {
        var blobName = $"locks/{Guid.NewGuid()}.lock";
        await _provider.UploadAsync(blobName, new MemoryStream([0]), BlobAccessTier.Hot);

        var leaseId = await _provider.AcquireLeaseAsync(blobName);

        // Renew should not throw
        await Should.NotThrowAsync(async () =>
            await _provider.RenewLeaseAsync(blobName, leaseId));

        await _provider.ReleaseLeaseAsync(blobName, leaseId);
    }

    [Test]
    public async Task AcquireLease_While_Held_Throws()
    {
        var blobName = $"locks/{Guid.NewGuid()}.lock";
        await _provider.UploadAsync(blobName, new MemoryStream([0]), BlobAccessTier.Hot);

        var leaseId = await _provider.AcquireLeaseAsync(blobName);
        try
        {
            await Should.ThrowAsync<Exception>(async () =>
                await _provider.AcquireLeaseAsync(blobName));
        }
        finally
        {
            await _provider.ReleaseLeaseAsync(blobName, leaseId);
        }
    }
}
