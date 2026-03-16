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
/// 8.3 — 1000 files with ~30% content overlap, backed up at high parallelism,
///        then fully restored and verified byte-for-byte.
/// 8.4 — Files that become unreadable mid-operation should emit BackupFileError
///        events and allow processing to continue for the remaining files.
/// </summary>
public class BackupPipelineStressTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 8.3  1000 files with ~30% overlap → backup + restore + byte-verify
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    [Timeout(300_000)]
    public async Task Backup_1000FilesWithOverlap_RestoreMatchesOriginal(CancellationToken ct)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string containerName = "arius-stress-backup";
        await new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName).CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var src = Path.Combine(Path.GetTempPath(), "arius-stress-src", Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "arius-stress-dst", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        const string passphrase = "stress-test-passphrase";
        const int fileCount     = 1000;
        const int uniqueCount   = 700; // ~30% overlap = 300 duplicates of existing content

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Generate unique content blocks
            var contentBlocks = new byte[uniqueCount][];
            for (int i = 0; i < uniqueCount; i++)
            {
                contentBlocks[i] = new byte[2048];
                RandomNumberGenerator.Fill(contentBlocks[i]);
            }

            // Write 1000 files — first uniqueCount are unique, rest are copies from random unique blocks
            var rng       = new Random(42);
            var fileBytes = new Dictionary<string, byte[]>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                var content = i < uniqueCount
                    ? contentBlocks[i]
                    : contentBlocks[rng.Next(uniqueCount)];
                var relativePath = $"file_{i:D4}.bin";
                var fullPath     = Path.Combine(src, relativePath);
                await File.WriteAllBytesAsync(fullPath, content, ct);
                fileBytes[relativePath] = content;
            }

            // Backup with high parallelism
            var parallelism = new ParallelismOptions(MaxFileProcessors: 8, MaxSealWorkers: 4,
                MaxUploaders: 4, MaxDownloaders: 4, MaxAssemblers: 4);
            var backupEvents = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, containerName, passphrase, [src], Parallelism: parallelism), ct))
                backupEvents.Add(e);

            var completed = backupEvents.OfType<BackupCompleted>().ShouldHaveSingleItem();
            completed.StoredFiles.ShouldBeGreaterThan(0);
            completed.Failed.ShouldBe(0);
            completed.TotalChunks.ShouldBeGreaterThan(0);
            // Deduplication should have occurred for the ~30% overlapping files
            completed.DeduplicatedFiles.ShouldBeGreaterThan(0,
                "at least some files should have been deduplicated");

            // 8.7 — Index uniqueness: every blob hash must map to exactly one pack entry
            var repoForIndex = factory(cs, containerName);
            _ = await repoForIndex.TryUnlockAsync(passphrase);
            var index = await repoForIndex.LoadIndexAsync();
            index.Keys.Distinct(StringComparer.Ordinal).Count()
                .ShouldBe(index.Count, "index must have no duplicate blob hash keys after parallel backup");

            var snap = completed.Snapshot!;

            // Restore with same high parallelism
            var restoreEvents = new List<RestoreEvent>();
            await foreach (var e in new RestoreHandler(factory).Handle(
                new RestoreRequest(cs, containerName, passphrase, snap.Id.Value, dst,
                    Parallelism: parallelism), ct))
                restoreEvents.Add(e);

            var restoreCompleted = restoreEvents.OfType<RestoreCompleted>().ShouldHaveSingleItem();
            restoreCompleted.RestoredFiles.ShouldBe(fileCount);
            restoreCompleted.Failed.ShouldBe(0);

            // Byte-level verification for all files
            var restoredFiles = Directory.GetFiles(dst, "*.bin", SearchOption.AllDirectories);
            restoredFiles.Length.ShouldBe(fileCount);

            foreach (var restoredFile in restoredFiles)
            {
                var name = Path.GetFileName(restoredFile);
                fileBytes.TryGetValue(name, out var expected).ShouldBeTrue($"unexpected file '{name}' in restore output");
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
    // 8.4  Files that become unreadable mid-operation → BackupFileError emitted,
    //       processing continues for the rest
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    [Timeout(120_000)]
    public async Task Backup_UnreadableFiles_EmitsErrorsAndContinues(CancellationToken ct)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string containerName = "arius-unreadable";
        await new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName).CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var src = Path.Combine(Path.GetTempPath(), "arius-unreadable-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        const string passphrase = "test-passphrase";

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Write 10 readable files
            for (int i = 0; i < 10; i++)
            {
                var content = new byte[512];
                RandomNumberGenerator.Fill(content);
                await File.WriteAllBytesAsync(Path.Combine(src, $"good_{i}.bin"), content, ct);
            }

            // Write 3 files then lock them to simulate unreadable files
            var lockedStreams = new List<FileStream>();
            for (int i = 0; i < 3; i++)
            {
                var path = Path.Combine(src, $"locked_{i}.bin");
                await File.WriteAllBytesAsync(path, new byte[256], ct);
                // Open with exclusive lock so the backup pipeline can't read it
                var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                lockedStreams.Add(fs);
            }

            try
            {
                var events = new List<BackupEvent>();
                await foreach (var e in new BackupHandler(factory).Handle(
                    new BackupRequest(cs, containerName, passphrase, [src]), ct))
                    events.Add(e);

                var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
                var errors    = events.OfType<BackupFileError>().ToList();

                // The 3 locked files should have generated errors
                errors.Count.ShouldBe(3, "one BackupFileError per locked file");

                // The 10 readable files should have been processed
                completed.StoredFiles.ShouldBe(10);
                completed.Failed.ShouldBe(3);
            }
            finally
            {
                foreach (var fs in lockedStreams) fs.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true);
        }
    }
}
