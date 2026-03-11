using System.Security.Cryptography;
using Arius.Azure;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Azure.Tests;

/// <summary>
/// Integration tests for <see cref="AzureBlobRepositoryStore"/> backed by
/// a local Azurite container.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class AzureBlobRepositoryStoreTests(AzuriteFixture fx)
{
    private const string Passphrase = "correct-horse-battery-staple";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<AzureBlobStorageProvider> CreateProviderAsync(string containerName)
    {
        var blobServiceClient = await fx.GetBlobServiceClientAsync();
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        return new AzureBlobStorageProvider(containerClient);
    }

    private static string WriteFile(string dir, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    private static byte[] RandomBytes(int length)
    {
        var buf = new byte[length];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full round-trip: init → backup (Cold tier) → restore.
    /// Restored files must match the originals byte-for-byte.
    /// </summary>
    [Test]
    public async Task InitBackupRestore_RoundTrip_FilesMatchByteForByte()
    {
        var root       = Path.Combine(Path.GetTempPath(), "arius-azure-tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source");
        var restorePath = Path.Combine(root, "restore");

        try
        {
            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(restorePath);

            // Write two source files with known content.
            var content1 = RandomBytes(4096);
            var content2 = RandomBytes(8192);
            WriteFile(sourcePath, "alpha.txt", content1);
            WriteFile(sourcePath, "sub/beta.txt", content2);

            var containerName = $"arius-test-{Guid.NewGuid():N}";
            var provider = await CreateProviderAsync(containerName);
            var store    = new AzureBlobRepositoryStore();

            // Init.
            var repoId = await store.InitAsync(provider, Passphrase);
            repoId.Value.ShouldNotBeNullOrEmpty();

            // Backup (Cold tier so restore works immediately without rehydration).
            var inputPaths = new[] { sourcePath };
            var (snapshot, stored, deduped) = await store.BackupAsync(
                provider, Passphrase, inputPaths, BlobTier.Cold);

            stored.ShouldBe(2);
            deduped.ShouldBe(0);
            snapshot.Id.Value.ShouldNotBeNullOrEmpty();

            // Plan restore.
            var (files, totalBytes) = await store.PlanRestoreAsync(
                provider, Passphrase, snapshot.Id.Value, null);

            files.Count.ShouldBe(2);
            totalBytes.ShouldBeGreaterThan(0);

            // Restore each file.
            foreach (var file in files)
            {
                await store.RestoreFileAsync(provider, Passphrase, file, restorePath);
            }

            // Verify content byte-for-byte.
            var alphaRestored = Directory.GetFiles(restorePath, "alpha.txt", SearchOption.AllDirectories)
                .SingleOrDefault();
            alphaRestored.ShouldNotBeNull("alpha.txt should be restored");
            File.ReadAllBytes(alphaRestored).ShouldBe(content1);

            var betaRestored = Directory.GetFiles(restorePath, "beta.txt", SearchOption.AllDirectories)
                .SingleOrDefault();
            betaRestored.ShouldNotBeNull("beta.txt should be restored");
            File.ReadAllBytes(betaRestored).ShouldBe(content2);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Second backup of the same unchanged files must produce no new pack blobs
    /// (full deduplication).
    /// </summary>
    [Test]
    public async Task SecondBackup_NoChanges_NoNewPackBlobsUploaded()
    {
        var root       = Path.Combine(Path.GetTempPath(), "arius-azure-tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source");

        try
        {
            Directory.CreateDirectory(sourcePath);
            WriteFile(sourcePath, "file.bin", RandomBytes(2048));

            var containerName = $"arius-test-{Guid.NewGuid():N}";
            var provider = await CreateProviderAsync(containerName);
            var store    = new AzureBlobRepositoryStore();

            await store.InitAsync(provider, Passphrase);

            var inputPaths = new[] { sourcePath };

            // First backup — stores the file.
            var (_, stored1, deduped1) = await store.BackupAsync(
                provider, Passphrase, inputPaths, BlobTier.Cold);
            stored1.ShouldBe(1);
            deduped1.ShouldBe(0);

            // Count pack blobs after first backup.
            var packsBefore = await CountPackBlobsAsync(provider);

            // Second backup — same file, no changes.
            var (_, stored2, deduped2) = await store.BackupAsync(
                provider, Passphrase, inputPaths, BlobTier.Cold);
            stored2.ShouldBe(0);
            deduped2.ShouldBe(1);

            // No new pack blobs should have been uploaded.
            var packsAfter = await CountPackBlobsAsync(provider);
            packsAfter.ShouldBe(packsBefore);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> CountPackBlobsAsync(AzureBlobStorageProvider provider)
    {
        var count = 0;
        await foreach (var _ in provider.ListAsync("data/"))
            count++;
        return count;
    }
}
