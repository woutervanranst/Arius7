using System.Security.Cryptography;
using Arius.Azure;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Init;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Shouldly;
using Testcontainers.Azurite;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

file static class TestHelpers
{
    public static string WriteFile(string dir, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    public static byte[] RandomBytes(int length)
    {
        var buf = new byte[length];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared fixture — Azurite container + temp source dir, shared per session
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RepoFixture : IAsyncInitializer, IAsyncDisposable
{
    private AzuriteContainer _azurite = null!;
    private const string ContainerName = "arius-test-workflow";

    public const string Passphrase      = "correct-horse-battery-staple";
    public const string WrongPassphrase = "wrong-passphrase";

    public string SourcePath { get; private set; } = null!;

    // State captured across ordered tests
    public Snapshot? FirstSnapshot  { get; set; }
    public Snapshot? SecondSnapshot { get; set; }

    public async Task InitializeAsync()
    {
        _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();
        await _azurite.StartAsync();

        // Create the test container
        var client = new global::Azure.Storage.Blobs.BlobContainerClient(
            _azurite.GetConnectionString(), ContainerName);
        await client.CreateIfNotExistsAsync();

        SourcePath = Path.Combine(Path.GetTempPath(), "arius-tests", Guid.NewGuid().ToString("N"), "source");
        Directory.CreateDirectory(SourcePath);
    }

    public string ConnectionString => _azurite.GetConnectionString();
    public string Container        => ContainerName;

    /// <summary>Creates a new <see cref="AzureRepository"/> pointing at the Azurite container.</summary>
    public AzureRepository CreateRepo()
        => new(new AzureBlobStorageProvider(ConnectionString, Container));

    /// <summary>Creates the repo factory delegate used by all handlers.</summary>
    public Func<string, string, AzureRepository> RepoFactory()
        => (connStr, container) => new AzureRepository(new AzureBlobStorageProvider(connStr, container));

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path.GetDirectoryName(SourcePath)))
            await Task.Run(() => Directory.Delete(Path.GetDirectoryName(SourcePath)!, recursive: true));
        await _azurite.DisposeAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Workflow tests — ordered, share a single RepoFixture per session
// ─────────────────────────────────────────────────────────────────────────────

[ClassDataSource<RepoFixture>(Shared = SharedType.PerTestSession)]
public class RepositoryWorkflowTests(RepoFixture fx)
{
    // ═════════════════════════════════════════════════════════════════════════
    // 1. Init — writes config + key blobs to Azurite
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Init_CreatesConfigAndKeyBlobs()
    {
        var handler = new InitHandler(fx.RepoFactory());
        var result  = await handler.Handle(new InitRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase));

        result.RepoId.Value.ShouldNotBeNullOrEmpty();
        result.ConfigBlobName.ShouldBe("config");
        result.KeyBlobName.ShouldStartWith("keys/");

        // Verify blobs actually exist in Azurite
        var repo = fx.CreateRepo();
        (await repo.LoadConfigAsync()).RepoId.ShouldBe(result.RepoId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. Passphrase validation
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Init_CreatesConfigAndKeyBlobs))]
    public async Task Init_CorrectPassphrase_Unlocks()
    {
        var repo = fx.CreateRepo();
        var key  = await repo.TryUnlockAsync(RepoFixture.Passphrase);
        key.ShouldNotBeNull();
        key!.Length.ShouldBe(32);
    }

    [Test]
    [DependsOn(nameof(Init_CreatesConfigAndKeyBlobs))]
    public async Task Init_WrongPassphrase_ReturnsNull()
    {
        var repo = fx.CreateRepo();
        var key  = await repo.TryUnlockAsync(RepoFixture.WrongPassphrase);
        key.ShouldBeNull();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. First backup — stores all files, emits correct events
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Init_CreatesConfigAndKeyBlobs))]
    public async Task Backup_FirstBackup_StoresAllFiles()
    {
        TestHelpers.WriteFile(fx.SourcePath, "a.txt",     TestHelpers.RandomBytes(512));
        TestHelpers.WriteFile(fx.SourcePath, "sub/b.txt", TestHelpers.RandomBytes(1024));

        var handler = new BackupHandler(fx.RepoFactory());
        var events  = new List<BackupEvent>();

        await foreach (var e in handler.Handle(
            new BackupRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase, [fx.SourcePath])))
            events.Add(e);

        events.OfType<BackupStarted>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(2);

        events.OfType<BackupFileProcessed>().Count().ShouldBe(2);
        events.OfType<BackupFileProcessed>().ShouldAllBe(e => !e.IsDeduplicated);

        var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
        completed.StoredFiles.ShouldBe(2);
        completed.DeduplicatedFiles.ShouldBe(0);
        completed.Failed.ShouldBe(0);
        completed.TotalChunks.ShouldBeGreaterThan(0);
        completed.NewChunks.ShouldBeGreaterThan(0);
        completed.DeduplicatedChunks.ShouldBe(0);
        completed.TotalBytes.ShouldBeGreaterThan(0);
        completed.NewBytes.ShouldBeGreaterThan(0);

        // Verify snapshot blob was written
        var repo = fx.CreateRepo();
        var snapshotDocs = new List<BackupSnapshotDocument>();
        await foreach (var doc in repo.ListSnapshotDocumentsAsync())
            snapshotDocs.Add(doc);
        snapshotDocs.Count.ShouldBe(1);

        fx.FirstSnapshot = completed.Snapshot;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. Snapshots list
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Snapshots_AfterFirstBackup_ReturnsOneSnapshot()
    {
        var handler   = new SnapshotsHandler(fx.RepoFactory());
        var snapshots = new List<Snapshot>();

        await foreach (var s in handler.Handle(
            new ListSnapshotsRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase)))
            snapshots.Add(s);

        snapshots.Count.ShouldBe(1);
        snapshots[0].Id.ShouldBe(fx.FirstSnapshot!.Id);
        snapshots[0].Hostname.ShouldBe(Environment.MachineName);
        snapshots[0].Username.ShouldBe(Environment.UserName);
    }

    [Test]
    [DependsOn(nameof(Init_CreatesConfigAndKeyBlobs))]
    public async Task Snapshots_WrongPassphrase_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var handler = new SnapshotsHandler(fx.RepoFactory());
            await foreach (var _ in handler.Handle(
                new ListSnapshotsRequest(fx.ConnectionString, fx.Container, RepoFixture.WrongPassphrase)))
            { }
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. Second backup — incremental dedup
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Backup_SecondBackup_DeduplicatesUnchangedFiles()
    {
        // One new file; the two originals are already in the repo
        TestHelpers.WriteFile(fx.SourcePath, "c.txt", TestHelpers.RandomBytes(256));

        var handler = new BackupHandler(fx.RepoFactory());
        var events  = new List<BackupEvent>();

        await foreach (var e in handler.Handle(
            new BackupRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase, [fx.SourcePath])))
            events.Add(e);

        events.OfType<BackupStarted>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(3);

        var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
        completed.StoredFiles.ShouldBe(1);       // only c.txt is new
        completed.DeduplicatedFiles.ShouldBe(2); // a.txt + sub/b.txt are deduped
        completed.Failed.ShouldBe(0);
        completed.TotalChunks.ShouldBeGreaterThan(0);
        completed.DeduplicatedChunks.ShouldBeGreaterThan(0);

        // Two snapshots total now
        var repo = fx.CreateRepo();
        var snapshotDocs = new List<BackupSnapshotDocument>();
        await foreach (var doc in repo.ListSnapshotDocumentsAsync())
            snapshotDocs.Add(doc);
        snapshotDocs.Count.ShouldBe(2);

        fx.SecondSnapshot = completed.Snapshot;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 6. Full restore — bytes match originals
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_FullRestore_FilesMatchOriginal()
    {
        var restorePath = Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new RestoreHandler(fx.RepoFactory());
            var events  = new List<RestoreEvent>();

            await foreach (var e in handler.Handle(
                new RestoreRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase,
                    fx.FirstSnapshot!.Id.Value, restorePath)))
                events.Add(e);

            events.OfType<RestorePlanReady>().ShouldHaveSingleItem()
                  .TotalFiles.ShouldBe(2);
            events.OfType<RestorePlanReady>().ShouldHaveSingleItem()
                  .PacksToDownload.ShouldBeGreaterThan(0);

            events.OfType<RestoreFileRestored>().Count().ShouldBe(2);

            var completed = events.OfType<RestoreCompleted>().ShouldHaveSingleItem();
            completed.RestoredFiles.ShouldBe(2);
            completed.Failed.ShouldBe(0);

            // Verify bytes match originals (match by filename)
            var restoredFiles = Directory.GetFiles(restorePath, "*", SearchOption.AllDirectories);
            restoredFiles.Length.ShouldBe(2);

            foreach (var restoredFile in restoredFiles)
            {
                var fileName  = Path.GetFileName(restoredFile);
                var originals = Directory.GetFiles(fx.SourcePath, fileName, SearchOption.AllDirectories);
                originals.ShouldNotBeEmpty($"No original found for '{fileName}'");

                var restoredBytes = await File.ReadAllBytesAsync(restoredFile);
                var originalBytes = await File.ReadAllBytesAsync(originals[0]);
                restoredBytes.ShouldBe(originalBytes);
            }
        }
        finally
        {
            if (Directory.Exists(restorePath))
                Directory.Delete(restorePath, recursive: true);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 7. Restore — snapshot ID prefix matching
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_BySnapshotIdPrefix_Works()
    {
        var restorePath = Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));
        try
        {
            var prefix  = fx.FirstSnapshot!.Id.Value[..8];
            var handler = new RestoreHandler(fx.RepoFactory());
            var events  = new List<RestoreEvent>();

            await foreach (var e in handler.Handle(
                new RestoreRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase,
                    prefix, restorePath)))
                events.Add(e);

            events.OfType<RestoreCompleted>().ShouldHaveSingleItem()
                  .RestoredFiles.ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(restorePath))
                Directory.Delete(restorePath, recursive: true);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 8. Restore — include filter
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_WithIncludeFilter_RestoresOnlyMatchingFiles()
    {
        var restorePath = Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new RestoreHandler(fx.RepoFactory());
            var events  = new List<RestoreEvent>();

            await foreach (var e in handler.Handle(
                new RestoreRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase,
                    fx.FirstSnapshot!.Id.Value, restorePath, Include: "a.txt")))
                events.Add(e);

            events.OfType<RestorePlanReady>().ShouldHaveSingleItem()
                  .TotalFiles.ShouldBe(1);
            events.OfType<RestoreCompleted>().ShouldHaveSingleItem()
                  .RestoredFiles.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(restorePath))
                Directory.Delete(restorePath, recursive: true);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 9. Restore — error cases
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_WrongPassphrase_Throws()
    {
        var restorePath = Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                var handler = new RestoreHandler(fx.RepoFactory());
                await foreach (var _ in handler.Handle(
                    new RestoreRequest(fx.ConnectionString, fx.Container, RepoFixture.WrongPassphrase,
                        fx.FirstSnapshot!.Id.Value, restorePath)))
                { }
            });
        }
        finally
        {
            if (Directory.Exists(restorePath))
                Directory.Delete(restorePath, recursive: true);
        }
    }

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_UnknownSnapshotId_Throws()
    {
        var restorePath = Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                var handler = new RestoreHandler(fx.RepoFactory());
                await foreach (var _ in handler.Handle(
                    new RestoreRequest(fx.ConnectionString, fx.Container, RepoFixture.Passphrase,
                        "nonexistent000000", restorePath)))
                { }
            });
        }
        finally
        {
            if (Directory.Exists(restorePath))
                Directory.Delete(restorePath, recursive: true);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Isolated dedup test (its own clean Azurite container)
// ─────────────────────────────────────────────────────────────────────────────

public class DeduplicationTests
{
    [Test]
    public async Task Backup_IdenticalContent_StoredOnlyOnce()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();
        await azurite.StartAsync();

        const string containerName = "arius-dedup-test";
        var containerClient = new global::Azure.Storage.Blobs.BlobContainerClient(
            azurite.GetConnectionString(), containerName);
        await containerClient.CreateIfNotExistsAsync();

        var connStr = azurite.GetConnectionString();
        Func<string, string, AzureRepository> factory =
            (cs, c) => new AzureRepository(new AzureBlobStorageProvider(cs, c));

        var srcPath = Path.Combine(Path.GetTempPath(), "arius-dedup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcPath);
        try
        {
            var content = TestHelpers.RandomBytes(512);
            TestHelpers.WriteFile(srcPath, "copy1.txt", content);
            TestHelpers.WriteFile(srcPath, "copy2.txt", content); // identical bytes

            await new InitHandler(factory).Handle(
                new InitRequest(connStr, containerName, RepoFixture.Passphrase));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler(factory).Handle(
                new BackupRequest(connStr, containerName, RepoFixture.Passphrase, [srcPath])))
                events.Add(e);

            var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
            completed.StoredFiles.ShouldBe(1);
            completed.DeduplicatedFiles.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(srcPath)) Directory.Delete(srcPath, recursive: true);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BlobHash unit tests (pure, no I/O)
// ─────────────────────────────────────────────────────────────────────────────

public class BlobHashTests
{
    private static readonly byte[] TestKey = new byte[32]; // all-zero key for determinism

    [Test]
    public void SameContent_SameHash()
    {
        var content = TestHelpers.RandomBytes(1024);
        BlobHash.FromBytes(content, TestKey).ShouldBe(BlobHash.FromBytes(content, TestKey));
    }

    [Test]
    public void DifferentContent_DifferentHash()
    {
        BlobHash.FromBytes(TestHelpers.RandomBytes(64), TestKey)
                .ShouldNotBe(BlobHash.FromBytes(TestHelpers.RandomBytes(64), TestKey));
    }

    [Test]
    public void DifferentKey_SameContent_DifferentHash()
    {
        var content = TestHelpers.RandomBytes(64);
        var key1    = TestHelpers.RandomBytes(32);
        var key2    = TestHelpers.RandomBytes(32);
        BlobHash.FromBytes(content, key1).ShouldNotBe(BlobHash.FromBytes(content, key2));
    }

    [Test]
    public void KnownVector_MatchesHmacSha256OfEmptyInput()
    {
        // HMAC-SHA256(key=0x00*32, data=[]) = known constant
        var expected = System.Security.Cryptography.HMACSHA256.HashData(TestKey, (byte[])[]);
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();
        BlobHash.FromBytes([], TestKey).Value.ShouldBe(expectedHex);
    }
}
