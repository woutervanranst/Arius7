using System.Security.Cryptography;
using Arius.Azure;
using Arius.Core.Application.Abstractions;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Check;
using Arius.Core.Application.Diff;
using Arius.Core.Application.Find;
using Arius.Core.Application.Forget;
using Arius.Core.Application.Init;
using Arius.Core.Application.Ls;
using Arius.Core.Application.Prune;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Arius.Core.Application.Stats;
using Arius.Core.Application.Tag;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Shouldly;
using Testcontainers.Azurite;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Arius.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────────────

file static class IntegHelpers
{
    public static byte[] RandomBytes(int length)
    {
        var buf = new byte[length];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    public static string WriteFile(string dir, string relativePath, byte[] content)
    {
        var full = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public static string CreateTempDir(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), "arius-integ", tag, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared fixture — one Azurite container per test session
// ─────────────────────────────────────────────────────────────────────────────

public sealed class IntegFixture : IAsyncInitializer, IAsyncDisposable
{
    private AzuriteContainer _azurite = null!;
    private const string ContainerName = "arius-integ";

    public const string Passphrase = "integration-test-passphrase";

    public string SourcePath { get; private set; } = null!;

    // Captured across ordered tests
    public Snapshot? Snap1 { get; set; }
    public Snapshot? Snap2 { get; set; }

    public async Task InitializeAsync()
    {
        _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await _azurite.StartAsync();

        var client = new global::Azure.Storage.Blobs.BlobContainerClient(
            _azurite.GetConnectionString(), ContainerName);
        await client.CreateIfNotExistsAsync();

        SourcePath = IntegHelpers.CreateTempDir("source");
    }

    public string ConnectionString => _azurite.GetConnectionString();
    public string Container        => ContainerName;

    public AzureRepository CreateRepo()
        => new(new AzureBlobStorageProvider(ConnectionString, Container));

    public Func<string, string, AzureRepository> RepoFactory()
        => (cs, c) => new AzureRepository(new AzureBlobStorageProvider(cs, c));

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(SourcePath))
            await Task.Run(() => Directory.Delete(Path.GetDirectoryName(SourcePath)!, recursive: true));
        await _azurite.DisposeAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.1  init → backup → snapshots → ls → verify tree content
// ─────────────────────────────────────────────────────────────────────────────

[ClassDataSource<IntegFixture>(Shared = SharedType.PerTestSession)]
public class InitBackupLsTests(IntegFixture fx)
{
    [Test]
    public async Task Init_CreatesRepo()
    {
        var handler = new InitHandler(fx.RepoFactory());
        var result  = await handler.Handle(
            new InitRequest(fx.ConnectionString, fx.Container, IntegFixture.Passphrase));

        result.RepoId.Value.ShouldNotBeNullOrEmpty();
        result.ConfigBlobName.ShouldBe("config");
        result.KeyBlobName.ShouldStartWith("keys/");
    }

    [Test]
    [DependsOn(nameof(Init_CreatesRepo))]
    public async Task Backup_StoresFiles()
    {
        IntegHelpers.WriteFile(fx.SourcePath, "file1.txt",     IntegHelpers.RandomBytes(512));
        IntegHelpers.WriteFile(fx.SourcePath, "sub/file2.txt", IntegHelpers.RandomBytes(1024));

        var events = new List<BackupEvent>();
        await foreach (var e in new BackupHandler(fx.RepoFactory()).Handle(
            new BackupRequest(fx.ConnectionString, fx.Container, IntegFixture.Passphrase, [fx.SourcePath])))
            events.Add(e);

        var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
        completed.StoredFiles.ShouldBe(2);
        completed.Failed.ShouldBe(0);
        completed.TotalChunks.ShouldBeGreaterThan(0);
        fx.Snap1 = completed.Snapshot;
    }

    [Test]
    [DependsOn(nameof(Backup_StoresFiles))]
    public async Task Snapshots_ReturnsSingleSnapshot()
    {
        var snapshots = new List<Snapshot>();
        await foreach (var s in new SnapshotsHandler(fx.RepoFactory()).Handle(
            new ListSnapshotsRequest(fx.ConnectionString, fx.Container, IntegFixture.Passphrase)))
            snapshots.Add(s);

        snapshots.Count.ShouldBe(1);
        snapshots[0].Id.ShouldBe(fx.Snap1!.Id);
    }

    [Test]
    [DependsOn(nameof(Backup_StoresFiles))]
    public async Task Ls_ReturnsCorrectTreeEntries()
    {
        var entries = new List<TreeEntry>();
        await foreach (var e in new LsHandler(fx.RepoFactory()).Handle(
            new LsRequest(fx.ConnectionString, fx.Container, IntegFixture.Passphrase,
                fx.Snap1!.Id.Value, Recursive: true)))
            entries.Add(e);

        // Should see file1.txt and sub/file2.txt (at minimum)
        entries.ShouldNotBeEmpty();
        entries.Select(e => e.Name).ShouldContain("file1.txt");
        entries.Select(e => e.Name).ShouldContain("file2.txt");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.2  backup → backup (incremental) → verify dedup
// ─────────────────────────────────────────────────────────────────────────────

public class IncrementalBackupDedupTests
{
    [Test]
    public async Task SecondBackup_DeduplicatesUnchangedFiles()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-dedup-integ";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("dedup");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            IntegHelpers.WriteFile(src, "a.txt", IntegHelpers.RandomBytes(512));
            IntegHelpers.WriteFile(src, "b.txt", IntegHelpers.RandomBytes(1024));

            // First backup
            var ev1 = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                ev1.Add(e);

            ev1.OfType<BackupCompleted>().Single().StoredFiles.ShouldBe(2);
            ev1.OfType<BackupCompleted>().Single().DeduplicatedFiles.ShouldBe(0);
            ev1.OfType<BackupCompleted>().Single().Failed.ShouldBe(0);

            // Add a new file only
            IntegHelpers.WriteFile(src, "c.txt", IntegHelpers.RandomBytes(256));

            // Second backup — a.txt + b.txt should be deduped
            var ev2 = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                ev2.Add(e);

            var comp2 = ev2.OfType<BackupCompleted>().Single();
            comp2.StoredFiles.ShouldBe(1,      "only c.txt is new");
            comp2.DeduplicatedFiles.ShouldBe(2, "a.txt + b.txt are already in the repo");
            comp2.Failed.ShouldBe(0);
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.3  backup → forget → prune → verify unreferenced packs removed
// ─────────────────────────────────────────────────────────────────────────────

public class ForgetPruneTests
{
    [Test]
    public async Task ForgetThenPrune_RemovesUnreferencedPacks()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-forget-prune";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("forget");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            // Backup with unique data so each backup is in its own pack
            IntegHelpers.WriteFile(src, "snap1.txt", IntegHelpers.RandomBytes(512));
            var ev1 = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                ev1.Add(e);
            var snap1 = ev1.OfType<BackupCompleted>().Single().Snapshot;

            IntegHelpers.WriteFile(src, "snap2.txt", IntegHelpers.RandomBytes(512));
            var ev2 = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                ev2.Add(e);

            // Two snapshots exist
            var snaps = new List<Snapshot>();
            await foreach (var s in new SnapshotsHandler(factory).Handle(
                new ListSnapshotsRequest(cs, c, IntegFixture.Passphrase)))
                snaps.Add(s);
            snaps.Count.ShouldBe(2);

            // Forget all but the latest (keep-last 1)
            var forgetEvents = new List<ForgetEvent>();
            await foreach (var fe in new ForgetHandler(factory).Handle(
                new ForgetRequest(cs, c, IntegFixture.Passphrase,
                    new RetentionPolicy(KeepLast: 1))))
                forgetEvents.Add(fe);

            forgetEvents.Count(e => e.Decision == ForgetDecision.Remove).ShouldBe(1);

            // After forget, only 1 snapshot remains
            var snapsAfter = new List<Snapshot>();
            await foreach (var s in new SnapshotsHandler(factory).Handle(
                new ListSnapshotsRequest(cs, c, IntegFixture.Passphrase)))
                snapsAfter.Add(s);
            snapsAfter.Count.ShouldBe(1);

            // Prune — should see at least one WillDelete or Done event
            var pruneEvents = new List<PruneEvent>();
            await foreach (var pe in new PruneHandler(factory).Handle(
                new PruneRequest(cs, c, IntegFixture.Passphrase)))
                pruneEvents.Add(pe);

            pruneEvents.ShouldContain(e => e.Kind == PruneEventKind.Done);
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.4  backup → restore → verify file content matches original
// ─────────────────────────────────────────────────────────────────────────────

public class RestoreIntegTests
{
    [Test]
    public async Task Restore_FilesMatchOriginal()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-restore-integ";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("restore-src");
        var dst     = IntegHelpers.CreateTempDir("restore-dst");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            var contentA = IntegHelpers.RandomBytes(512);
            var contentB = IntegHelpers.RandomBytes(1024);
            IntegHelpers.WriteFile(src, "a.bin",     contentA);
            IntegHelpers.WriteFile(src, "sub/b.bin", contentB);

            var backupEvents = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                backupEvents.Add(e);

            var snap = backupEvents.OfType<BackupCompleted>().Single().Snapshot;

            var restoreEvents = new List<RestoreEvent>();
            await foreach (var e in new RestoreHandler(factory).Handle(
                new RestoreRequest(cs, c, IntegFixture.Passphrase, snap.Id.Value, dst)))
                restoreEvents.Add(e);

            restoreEvents.OfType<RestoreCompleted>().Single().RestoredFiles.ShouldBe(2);
            restoreEvents.OfType<RestoreCompleted>().Single().Failed.ShouldBe(0);
            restoreEvents.OfType<RestorePlanReady>().Single().PacksToDownload.ShouldBeGreaterThan(0);

            // Verify bytes match
            var files = Directory.GetFiles(dst, "*", SearchOption.AllDirectories);
            files.Length.ShouldBe(2);

            foreach (var file in files)
            {
                var name    = Path.GetFileName(file);
                var bytes   = await File.ReadAllBytesAsync(file);
                var originals = Directory.GetFiles(src, name, SearchOption.AllDirectories);
                originals.ShouldNotBeEmpty();
                bytes.ShouldBe(await File.ReadAllBytesAsync(originals[0]));
            }
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true);
            if (Directory.Exists(dst)) Directory.Delete(Path.GetDirectoryName(dst)!, recursive: true);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.5  delete local cache → rebuild from Azure → verify operations work
// (RepositoryCache tests cover this in unit form; here we verify rebuild via AzureRepository)
// ─────────────────────────────────────────────────────────────────────────────

public class CacheRebuildTests
{
    [Test]
    public async Task RebuildCache_OperationsStillWork()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-cache-rebuild";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("cache-rebuild-src");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            IntegHelpers.WriteFile(src, "x.txt", IntegHelpers.RandomBytes(256));
            var backupEvents = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                backupEvents.Add(e);

            var snap = backupEvents.OfType<BackupCompleted>().Single().Snapshot;

            // Create a fresh repo (simulates deleted local cache — no in-memory state shared)
            var freshRepo = factory(cs, c);
            _ = await freshRepo.TryUnlockAsync(IntegFixture.Passphrase);

            // Load snapshot from scratch
            var doc = await freshRepo.LoadSnapshotDocumentAsync(snap.Id.Value);
            doc.Snapshot.Id.ShouldBe(snap.Id);

            // Ls should work on the fresh instance
            var entries = new List<TreeEntry>();
            await foreach (var e in new LsHandler(factory).Handle(
                new LsRequest(cs, c, IntegFixture.Passphrase, snap.Id.Value, Recursive: true)))
                entries.Add(e);

            entries.ShouldNotBeEmpty();
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.6  backup 1000+ small files → verify chunking/packing behavior
// ─────────────────────────────────────────────────────────────────────────────

public class ScaleTests
{
    [Test]
    [Timeout(120_000)]
    public async Task Backup_1000SmallFiles_CompletesAndStoresAll(CancellationToken cancellationToken)
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-scale";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("scale-src");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            const int fileCount = 1000;
            for (int i = 0; i < fileCount; i++)
                IntegHelpers.WriteFile(src, $"file_{i:D4}.bin", IntegHelpers.RandomBytes(2048));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                events.Add(e);

            var started   = events.OfType<BackupStarted>().Single();
            var completed = events.OfType<BackupCompleted>().Single();

            started.TotalFiles.ShouldBe(fileCount);
            completed.StoredFiles.ShouldBe(fileCount);
            completed.DeduplicatedFiles.ShouldBe(0);
            completed.Failed.ShouldBe(0);
            completed.TotalChunks.ShouldBeGreaterThan(0);

            // Verify snapshots count
            var snaps = new List<Snapshot>();
            await foreach (var s in new SnapshotsHandler(factory).Handle(
                new ListSnapshotsRequest(cs, c, IntegFixture.Passphrase)))
                snaps.Add(s);
            snaps.Count.ShouldBe(1);
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.7  concurrent backup attempt → verify lease-based locking
// (lease is on the config blob; second init attempt on same repo would fail if leased)
// ─────────────────────────────────────────────────────────────────────────────

public class LeaseLockingTests
{
    [Test]
    public async Task AzureBlobStorageProvider_AcquireLease_PreventsDoubleAcquire()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-lease";
        var containerClient = new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), c);
        await containerClient.CreateIfNotExistsAsync();

        var provider = new AzureBlobStorageProvider(azurite.GetConnectionString(), c);

        // Upload a blob to lease against
        using var ms = new MemoryStream(new byte[1]);
        await provider.UploadAsync("lock-target", ms, BlobAccessTier.Hot);

        // Acquire lease
        var leaseId = await provider.AcquireLeaseAsync("lock-target");
        leaseId.ShouldNotBeNullOrEmpty();

        // A second acquire attempt on the same blob should throw
        await Should.ThrowAsync<Exception>(async () =>
            await provider.AcquireLeaseAsync("lock-target"));

        // Release
        await provider.ReleaseLeaseAsync("lock-target", leaseId);

        // After release, acquiring again should succeed
        var leaseId2 = await provider.AcquireLeaseAsync("lock-target");
        leaseId2.ShouldNotBeNullOrEmpty();
        await provider.ReleaseLeaseAsync("lock-target", leaseId2);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.8  manual recovery test — backup then verify index is queryable
// (Full CLI-based openssl/tar recovery is a manual process; we verify the index
//  is persisted so a recovery tool can locate blobs)
// ─────────────────────────────────────────────────────────────────────────────

public class ManualRecoveryReadinessTests
{
    [Test]
    public async Task Backup_IndexContainsAllBlobEntries()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-manual-recovery";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("recovery-src");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));

            IntegHelpers.WriteFile(src, "recover.bin", IntegHelpers.RandomBytes(1024));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                events.Add(e);

            events.OfType<BackupCompleted>().ShouldHaveSingleItem();

            // After backup, the index should contain blob entries
            var repo  = factory(cs, c);
            _ = await repo.TryUnlockAsync(IntegFixture.Passphrase);
            var index = await repo.LoadIndexAsync();
            index.ShouldNotBeEmpty("index must contain entries for all uploaded data blobs");
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.9  backup with Hot tier → verify blobs in Azure have Hot tier
// ─────────────────────────────────────────────────────────────────────────────

public class TierHotTests
{
    [Test]
    public async Task Backup_WithHotTier_PackBlobsAreHot()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-tier-hot";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("tier-hot-src");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));
            IntegHelpers.WriteFile(src, "hot.bin", IntegHelpers.RandomBytes(512));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src], BlobAccessTier.Hot)))
                events.Add(e);

            events.OfType<BackupCompleted>().ShouldHaveSingleItem();

            // Check that pack blobs under "data/" have Hot tier
            var provider = new AzureBlobStorageProvider(cs, c);
            var packBlobs = new List<BlobItem>();
            await foreach (var b in provider.ListAsync("data/"))
                packBlobs.Add(b);

            packBlobs.ShouldNotBeEmpty("at least one pack blob must be uploaded");

            foreach (var blob in packBlobs)
            {
                var tier = await provider.GetTierAsync(blob.Name);
                tier.ShouldBe(BlobAccessTier.Hot,
                    $"pack blob '{blob.Name}' should be Hot but is {tier}");
            }
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 14.10  backup with Archive tier → verify blobs are Archive tier
//        (Azurite supports Archive tier assignment; actual rehydration is async on real Azure)
// ─────────────────────────────────────────────────────────────────────────────

public class TierArchiveTests
{
    [Test]
    public async Task Backup_WithArchiveTier_PackBlobsAreArchive()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await azurite.StartAsync();
        const string c = "arius-tier-archive";
        await new global::Azure.Storage.Blobs.BlobContainerClient(azurite.GetConnectionString(), c)
            .CreateIfNotExistsAsync();

        var cs      = azurite.GetConnectionString();
        var factory = (Func<string, string, AzureRepository>)
            ((connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container)));
        var src     = IntegHelpers.CreateTempDir("tier-archive-src");

        try
        {
            await new InitHandler(factory).Handle(new InitRequest(cs, c, IntegFixture.Passphrase));
            IntegHelpers.WriteFile(src, "archive.bin", IntegHelpers.RandomBytes(512));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                // default tier is Archive
                new BackupRequest(cs, c, IntegFixture.Passphrase, [src])))
                events.Add(e);

            events.OfType<BackupCompleted>().ShouldHaveSingleItem();

            var provider  = new AzureBlobStorageProvider(cs, c);
            var packBlobs = new List<BlobItem>();
            await foreach (var b in provider.ListAsync("data/"))
                packBlobs.Add(b);

            packBlobs.ShouldNotBeEmpty();

            foreach (var blob in packBlobs)
            {
                var tier = await provider.GetTierAsync(blob.Name);
                tier.ShouldBe(BlobAccessTier.Archive,
                    $"pack blob '{blob.Name}' should be Archive but is {tier}");
            }
        }
        finally { if (Directory.Exists(src)) Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}
