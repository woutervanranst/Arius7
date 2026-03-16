using System.Security.Cryptography;
using Arius.Azure;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Init;
using Arius.Core.Application.Restore;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Shouldly;
using Testcontainers.Azurite;
using TUnit.Core;

namespace Arius.Core.Tests.Concurrency;

/// <summary>
/// 8.5 — Parallel pack download + assembly: backs up files with high parallelism,
///        restores with MaxDownloaders=4 and MaxAssemblers=8, verifies every byte.
/// 8.6 — Temp directory cleanup: verifies the temp dir is deleted after both
///        a successful restore and a restore that fails due to a bad snapshot id.
/// </summary>
public class RestorePipelineStressTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 8.5  Parallel pack download + assembly → all files byte-identical
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    [Timeout(300_000)]
    public async Task Restore_ParallelDownloadAndAssembly_AllFilesByteIdentical(CancellationToken ct)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();

        const string containerName = "arius-restore-stress";
        await new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName).CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var src = Path.Combine(Path.GetTempPath(), "arius-restore-stress-src", Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "arius-restore-stress-dst", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        const string passphrase  = "restore-stress-passphrase";
        const int    fileCount   = 200;
        const int    uniqueCount = 140; // ~30% overlap

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Generate content blocks and write files
            var contentBlocks = new byte[uniqueCount][];
            for (int i = 0; i < uniqueCount; i++)
            {
                contentBlocks[i] = new byte[3072];
                RandomNumberGenerator.Fill(contentBlocks[i]);
            }

            var rng       = new Random(99);
            var fileBytes = new Dictionary<string, byte[]>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                var content      = i < uniqueCount ? contentBlocks[i] : contentBlocks[rng.Next(uniqueCount)];
                var relativePath = $"file_{i:D4}.bin";
                await File.WriteAllBytesAsync(Path.Combine(src, relativePath), content, ct);
                fileBytes[relativePath] = content;
            }

            // Backup with moderate parallelism
            var parallelism = new ParallelismOptions(
                MaxFileProcessors: 4, MaxSealWorkers: 2, MaxUploaders: 2,
                MaxDownloaders: 4,    MaxAssemblers:  8);

            var backupEvents = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, containerName, passphrase, [src], Parallelism: parallelism), ct))
                backupEvents.Add(e);

            var backupCompleted = backupEvents.OfType<BackupCompleted>().ShouldHaveSingleItem();
            backupCompleted.Failed.ShouldBe(0);
            var snap = backupCompleted.Snapshot!;

            // Restore with high download + assembly parallelism
            var restoreEvents = new List<RestoreEvent>();
            await foreach (var e in new RestoreHandler(factory).Handle(
                new RestoreRequest(cs, containerName, passphrase, snap.Id.Value, dst,
                    Parallelism: parallelism), ct))
                restoreEvents.Add(e);

            var planReady = restoreEvents.OfType<RestorePlanReady>().ShouldHaveSingleItem();
            planReady.TotalFiles.ShouldBe(fileCount);
            planReady.PacksToDownload.ShouldBeGreaterThan(0);

            // One RestorePackFetched per pack
            restoreEvents.OfType<RestorePackFetched>().Count().ShouldBe(planReady.PacksToDownload);

            var restoreCompleted = restoreEvents.OfType<RestoreCompleted>().ShouldHaveSingleItem();
            restoreCompleted.RestoredFiles.ShouldBe(fileCount);
            restoreCompleted.Failed.ShouldBe(0);

            // Byte-level verification
            var restoredFiles = Directory.GetFiles(dst, "*.bin", SearchOption.AllDirectories);
            restoredFiles.Length.ShouldBe(fileCount);

            foreach (var restoredFile in restoredFiles)
            {
                var name = Path.GetFileName(restoredFile);
                fileBytes.TryGetValue(name, out var expected).ShouldBeTrue($"unexpected file '{name}'");
                var actual = await File.ReadAllBytesAsync(restoredFile, ct);
                actual.ShouldBe(expected!, $"byte mismatch for '{name}'");
            }
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true);
            if (Directory.Exists(dst)) Directory.Delete(Path.GetDirectoryName(dst)!, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8.6  Temp directory cleanup after successful restore AND after failure
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    [Timeout(120_000)]
    public async Task Restore_SuccessfulRestore_TempDirIsCleanedUp(CancellationToken ct)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();

        const string containerName = "arius-restore-cleanup-ok";
        await new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName).CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var src     = Path.Combine(Path.GetTempPath(), "arius-cleanup-src", Guid.NewGuid().ToString("N"));
        var dst     = Path.Combine(Path.GetTempPath(), "arius-cleanup-dst", Guid.NewGuid().ToString("N"));
        var tempDir = Path.Combine(Path.GetTempPath(), "arius-cleanup-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        const string passphrase = "cleanup-passphrase";

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Write a few small files
            for (int i = 0; i < 5; i++)
            {
                var content = new byte[512];
                RandomNumberGenerator.Fill(content);
                await File.WriteAllBytesAsync(Path.Combine(src, $"file_{i}.bin"), content, ct);
            }

            // Backup
            var backupEvents = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, containerName, passphrase, [src]), ct))
                backupEvents.Add(e);

            var snap = backupEvents.OfType<BackupCompleted>().ShouldHaveSingleItem().Snapshot!;

            // Restore with an explicit tempDir so we can inspect it afterwards
            var restoreEvents = new List<RestoreEvent>();
            await foreach (var e in new RestoreHandler(factory).Handle(
                new RestoreRequest(cs, containerName, passphrase, snap.Id.Value, dst,
                    TempPath: tempDir), ct))
                restoreEvents.Add(e);

            restoreEvents.OfType<RestoreCompleted>().ShouldHaveSingleItem().Failed.ShouldBe(0);

            // After a successful restore, the temp dir must be gone
            Directory.Exists(tempDir).ShouldBeFalse(
                "temp directory should be deleted after a successful restore");
        }
        finally
        {
            if (Directory.Exists(src))     Directory.Delete(Path.GetDirectoryName(src)!,     recursive: true);
            if (Directory.Exists(dst))     Directory.Delete(dst,     recursive: true);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task Restore_FailedRestore_TempDirIsStillCleanedUp(CancellationToken ct)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();

        const string containerName = "arius-restore-cleanup-fail";
        await new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName).CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var dst     = Path.Combine(Path.GetTempPath(), "arius-cleanup-fail-dst", Guid.NewGuid().ToString("N"));
        var tempDir = Path.Combine(Path.GetTempPath(), "arius-cleanup-fail-tmp", Guid.NewGuid().ToString("N"));

        const string passphrase = "cleanup-fail-passphrase";

        // Init the repo (so unlock succeeds) but supply a non-existent snapshot ID
        // → RestoreHandler will throw an InvalidOperationException
        var src = Path.Combine(Path.GetTempPath(), "arius-cleanup-fail-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Write one file and back it up so the repo is properly initialised
            await File.WriteAllBytesAsync(Path.Combine(src, "init.bin"), new byte[256], ct);
            await foreach (var _ in new BackupHandler(factory).Handle(
                new BackupRequest(cs, containerName, passphrase, [src]), ct)) { }

            // Attempt restore with a garbage snapshot id → should throw
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in new RestoreHandler(factory).Handle(
                    new RestoreRequest(cs, containerName, passphrase,
                        "nonexistentsnapshot00",
                        dst,
                        TempPath: tempDir), ct))
                { }
            });

            // Even after the failure, the temp dir must NOT exist (cleaned up in finally)
            Directory.Exists(tempDir).ShouldBeFalse(
                "temp directory should be deleted even after a failed restore");
        }
        finally
        {
            if (Directory.Exists(src))     Directory.Delete(Path.GetDirectoryName(src)!, recursive: true);
            if (Directory.Exists(dst))     Directory.Delete(dst,     recursive: true);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
