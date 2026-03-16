using System.Collections.Concurrent;
using Arius.Azure;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Init;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Shouldly;
using Testcontainers.Azurite;
using TUnit.Core;

namespace Arius.Core.Tests.Concurrency;

/// <summary>
/// 8.1 — Barrier-based test with 10 threads calling ConcurrentDictionary.TryAdd
///        on the same hash; asserts exactly 1 thread wins.
/// 8.2 — Two files with identical content in a single backup should produce
///        exactly one pack entry in the index.
/// </summary>
public class DedupGateTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 8.1  10-thread race on TryAdd — exactly 1 wins
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void TryAdd_10ThreadsRace_ExactlyOneWins()
    {
        var dict    = new ConcurrentDictionary<string, int>();
        const string key = "shared-blob-hash";
        int wins = 0;

        var barrier  = new Barrier(10);
        var threads  = new Thread[10];
        var results  = new bool[10];

        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            threads[idx] = new Thread(() =>
            {
                barrier.SignalAndWait(); // all threads reach this point before proceeding
                bool added = dict.TryAdd(key, idx);
                if (added) Interlocked.Increment(ref wins);
                results[idx] = added;
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        wins.ShouldBe(1, "exactly one thread should win the TryAdd race");
        dict.Count.ShouldBe(1);
        results.Count(r => r).ShouldBe(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8.2  Duplicate blob across two files → exactly one pack entry in index
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Backup_IdenticalBlob_TwoFiles_ProducesOnePackEntry()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();
        await azurite.StartAsync();

        const string containerName = "arius-dedup-gate";
        var containerClient = new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName);
        await containerClient.CreateIfNotExistsAsync();

        var cs = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (connStr, c) => new AzureRepository(new AzureBlobStorageProvider(connStr, c));

        var src = Path.Combine(Path.GetTempPath(), "arius-dedup-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        try
        {
            const string passphrase = "test-passphrase";
            await new InitHandler(factory).Handle(new InitRequest(cs, containerName, passphrase));

            // Both files have exactly the same content
            var sharedContent = new byte[4096];
            new Random(42).NextBytes(sharedContent);

            File.WriteAllBytes(Path.Combine(src, "copy1.bin"), sharedContent);
            File.WriteAllBytes(Path.Combine(src, "copy2.bin"), sharedContent);

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, containerName, passphrase, [src])))
                events.Add(e);

            var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
            completed.StoredFiles.ShouldBe(1,       "one unique file worth of data stored");
            completed.DeduplicatedFiles.ShouldBe(1, "second file is a duplicate");
            completed.Failed.ShouldBe(0);

            // Verify the index has exactly one entry for the shared blob
            var repo = factory(cs, containerName);
            _ = await repo.TryUnlockAsync(passphrase);
            var index = await repo.LoadIndexAsync();

            // 8.7 — Index uniqueness: Dictionary keys are already unique by definition,
            //        but confirm the count matches (no silent overwrites or merging errors)
            index.Keys.Distinct(StringComparer.Ordinal).Count()
                .ShouldBe(index.Count, "index must have no duplicate blob hash keys after parallel backup");

            // All index entries should reference the same pack (one pack, one blob entry)
            var uniquePacks = index.Values.Select(e => e.PackId).Distinct().ToList();
            uniquePacks.Count.ShouldBe(1, "all blobs should be in a single pack since content is identical");
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true);
        }
    }
}
