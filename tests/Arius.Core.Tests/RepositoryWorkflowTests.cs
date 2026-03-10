using System.Security.Cryptography;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Init;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Core.Tests;

/// <summary>
/// Shared fixture that holds repo + source paths for the workflow test class.
/// Using ClassDataSource(Shared = SharedType.PerTestSession) gives all test
/// methods in the class the SAME instance, so state set in one test is
/// visible in subsequent tests.
/// </summary>
public sealed class RepoFixture : IAsyncDisposable
{
    public string RepoPath   { get; }
    public string SourcePath { get; }
    public const string Passphrase      = "correct-horse-battery-staple";
    public const string WrongPassphrase = "wrong-passphrase";

    // State captured across ordered tests
    public Snapshot? FirstSnapshot  { get; set; }
    public Snapshot? SecondSnapshot { get; set; }

    public RepoFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "arius-tests", Guid.NewGuid().ToString("N"));
        RepoPath   = Path.Combine(root, "repo");
        SourcePath = Path.Combine(root, "source");
        Directory.CreateDirectory(SourcePath);
    }

    public async ValueTask DisposeAsync()
    {
        var root = Path.GetDirectoryName(RepoPath)!;
        if (Directory.Exists(root))
            await Task.Run(() => Directory.Delete(root, recursive: true));
    }
}

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

    /// <summary>Returns the number of .pack files written to the packs/ directory.</summary>
    public static int PackCount(string repoPath)
    {
        var packsDir = Path.Combine(repoPath, "packs");
        return Directory.Exists(packsDir)
            ? Directory.GetFiles(packsDir, "*.pack").Length
            : 0;
    }

    public static int SnapshotCount(string repoPath) =>
        Directory.GetFiles(Path.Combine(repoPath, "snapshots"), "*.json").Length;
}

// ─────────────────────────────────────────────────────────────────────────────
// Workflow tests — ordered, share a single RepoFixture per session
// ─────────────────────────────────────────────────────────────────────────────

[ClassDataSource<RepoFixture>(Shared = SharedType.PerTestSession)]
public class RepositoryWorkflowTests(RepoFixture fx)
{
    // ═════════════════════════════════════════════════════════════════════════
    // 1. Init — creates expected directory structure
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Init_CreatesExpectedRepoStructure()
    {
        var handler = new InitHandler();
        var result  = await handler.Handle(new InitRequest(fx.RepoPath, RepoFixture.Passphrase));

        result.RepoId.Value.ShouldNotBeNullOrEmpty();

        File.Exists(result.ConfigPath).ShouldBeTrue();
        result.ConfigPath.ShouldStartWith(fx.RepoPath);

        File.Exists(result.KeyPath).ShouldBeTrue();
        result.KeyPath.ShouldStartWith(fx.RepoPath);

        Directory.Exists(Path.Combine(fx.RepoPath, "packs")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fx.RepoPath, "index")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fx.RepoPath, "snapshots")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fx.RepoPath, "keys")).ShouldBeTrue();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. Passphrase validation
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Init_CreatesExpectedRepoStructure))]
    public async Task Init_CorrectPassphrase_ValidatesSuccessfully()
    {
        var store = new Arius.Core.Infrastructure.FileSystemRepositoryStore();
        var ok    = await store.ValidatePassphraseAsync(fx.RepoPath, RepoFixture.Passphrase);
        ok.ShouldBeTrue();
    }

    [Test]
    [DependsOn(nameof(Init_CreatesExpectedRepoStructure))]
    public async Task Init_WrongPassphrase_FailsValidation()
    {
        var store = new Arius.Core.Infrastructure.FileSystemRepositoryStore();
        var ok    = await store.ValidatePassphraseAsync(fx.RepoPath, RepoFixture.WrongPassphrase);
        ok.ShouldBeFalse();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. First backup — stores all files, emits correct events (task 14.1)
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Init_CreatesExpectedRepoStructure))]
    public async Task Backup_FirstBackup_StoresAllFiles()
    {
        TestHelpers.WriteFile(fx.SourcePath, "a.txt",     TestHelpers.RandomBytes(512));
        TestHelpers.WriteFile(fx.SourcePath, "sub/b.txt", TestHelpers.RandomBytes(1024));

        var handler = new BackupHandler();
        var events  = new List<BackupEvent>();

        await foreach (var e in handler.Handle(
            new BackupRequest(fx.RepoPath, RepoFixture.Passphrase, [fx.SourcePath])))
            events.Add(e);

        events.OfType<BackupStarted>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(2);

        events.OfType<BackupFileProcessed>().Count().ShouldBe(2);
        events.OfType<BackupFileProcessed>().ShouldAllBe(e => !e.IsDeduplicated);

        var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
        completed.StoredFiles.ShouldBe(2);
        completed.DeduplicatedFiles.ShouldBe(0);

        TestHelpers.SnapshotCount(fx.RepoPath).ShouldBe(1);
        TestHelpers.PackCount(fx.RepoPath).ShouldBe(1); // all 2 chunks land in one pack

        fx.FirstSnapshot = completed.Snapshot;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. Snapshots list (task 14.1)
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Snapshots_AfterFirstBackup_ReturnsOneSnapshot()
    {
        var handler   = new SnapshotsHandler();
        var snapshots = new List<Snapshot>();

        await foreach (var s in handler.Handle(
            new ListSnapshotsRequest(fx.RepoPath, RepoFixture.Passphrase)))
            snapshots.Add(s);

        snapshots.Count.ShouldBe(1);
        snapshots[0].Id.ShouldBe(fx.FirstSnapshot!.Id);
        snapshots[0].Hostname.ShouldBe(Environment.MachineName);
        snapshots[0].Username.ShouldBe(Environment.UserName);
    }

    [Test]
    [DependsOn(nameof(Init_CreatesExpectedRepoStructure))]
    public async Task Snapshots_WrongPassphrase_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var handler = new SnapshotsHandler();
            await foreach (var _ in handler.Handle(
                new ListSnapshotsRequest(fx.RepoPath, RepoFixture.WrongPassphrase)))
            { }
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. Second backup — incremental dedup (task 14.2)
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Backup_SecondBackup_DeduplicatesUnchangedFiles()
    {
        // One new file; the two originals are already in the repo
        TestHelpers.WriteFile(fx.SourcePath, "c.txt", TestHelpers.RandomBytes(256));

        var handler = new BackupHandler();
        var events  = new List<BackupEvent>();

        await foreach (var e in handler.Handle(
            new BackupRequest(fx.RepoPath, RepoFixture.Passphrase, [fx.SourcePath])))
            events.Add(e);

        events.OfType<BackupStarted>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(3);

        var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
        completed.StoredFiles.ShouldBe(1);       // only c.txt is new
        completed.DeduplicatedFiles.ShouldBe(2); // a.txt + sub/b.txt are deduped

        TestHelpers.PackCount(fx.RepoPath).ShouldBe(2);   // 1 from first backup + 1 from second
        TestHelpers.SnapshotCount(fx.RepoPath).ShouldBe(2);

        fx.SecondSnapshot = completed.Snapshot;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 6. Full restore — bytes match originals (task 14.4)
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_FullRestore_FilesMatchOriginal()
    {
        var restorePath = Path.Combine(Path.GetDirectoryName(fx.RepoPath)!, "restore-full");

        var handler = new RestoreHandler();
        var events  = new List<RestoreEvent>();

        await foreach (var e in handler.Handle(
            new RestoreRequest(fx.RepoPath, RepoFixture.Passphrase,
                fx.FirstSnapshot!.Id.Value, restorePath)))
            events.Add(e);

        events.OfType<RestorePlanReady>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(2);

        events.OfType<RestoreFileRestored>().Count().ShouldBe(2);

        var completed = events.OfType<RestoreCompleted>().ShouldHaveSingleItem();
        completed.RestoredFiles.ShouldBe(2);

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

    // ═════════════════════════════════════════════════════════════════════════
    // 7. Restore — snapshot ID prefix matching
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_BySnapshotIdPrefix_Works()
    {
        var restorePath = Path.Combine(Path.GetDirectoryName(fx.RepoPath)!, "restore-prefix");
        var prefix      = fx.FirstSnapshot!.Id.Value[..8];

        var handler = new RestoreHandler();
        var events  = new List<RestoreEvent>();

        await foreach (var e in handler.Handle(
            new RestoreRequest(fx.RepoPath, RepoFixture.Passphrase, prefix, restorePath)))
            events.Add(e);

        events.OfType<RestoreCompleted>().ShouldHaveSingleItem()
              .RestoredFiles.ShouldBe(2);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 8. Restore — include filter
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_WithIncludeFilter_RestoresOnlyMatchingFiles()
    {
        var restorePath = Path.Combine(Path.GetDirectoryName(fx.RepoPath)!, "restore-filtered");

        var handler = new RestoreHandler();
        var events  = new List<RestoreEvent>();

        await foreach (var e in handler.Handle(
            new RestoreRequest(fx.RepoPath, RepoFixture.Passphrase,
                fx.FirstSnapshot!.Id.Value, restorePath, Include: "a.txt")))
            events.Add(e);

        events.OfType<RestorePlanReady>().ShouldHaveSingleItem()
              .TotalFiles.ShouldBe(1);
        events.OfType<RestoreCompleted>().ShouldHaveSingleItem()
              .RestoredFiles.ShouldBe(1);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 9. Restore — error cases
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_WrongPassphrase_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var handler = new RestoreHandler();
            await foreach (var _ in handler.Handle(
                new RestoreRequest(fx.RepoPath, RepoFixture.WrongPassphrase,
                    fx.FirstSnapshot!.Id.Value,
                    Path.Combine(Path.GetDirectoryName(fx.RepoPath)!, "restore-bad"))))
            { }
        });
    }

    [Test]
    [DependsOn(nameof(Backup_FirstBackup_StoresAllFiles))]
    public async Task Restore_UnknownSnapshotId_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var handler = new RestoreHandler();
            await foreach (var _ in handler.Handle(
                new RestoreRequest(fx.RepoPath, RepoFixture.Passphrase,
                    "nonexistent000000",
                    Path.Combine(Path.GetDirectoryName(fx.RepoPath)!, "restore-ghost"))))
            { }
        });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Isolated dedup test (needs its own clean repo)
// ─────────────────────────────────────────────────────────────────────────────

public class DeduplicationTests
{
    [Test]
    public async Task Backup_IdenticalContent_StoredOnlyOnce()
    {
        var root      = Path.Combine(Path.GetTempPath(), "arius-tests", Guid.NewGuid().ToString("N"));
        var repoPath  = Path.Combine(root, "repo");
        var srcPath   = Path.Combine(root, "source");
        Directory.CreateDirectory(srcPath);

        try
        {
            var content = TestHelpers.RandomBytes(512);
            TestHelpers.WriteFile(srcPath, "copy1.txt", content);
            TestHelpers.WriteFile(srcPath, "copy2.txt", content); // identical bytes

            await new InitHandler().Handle(new InitRequest(repoPath, RepoFixture.Passphrase));

            var events = new List<BackupEvent>();
            await foreach (var e in new BackupHandler().Handle(
                new BackupRequest(repoPath, RepoFixture.Passphrase, [srcPath])))
                events.Add(e);

            var completed = events.OfType<BackupCompleted>().ShouldHaveSingleItem();
            completed.StoredFiles.ShouldBe(1);
            completed.DeduplicatedFiles.ShouldBe(1);
            TestHelpers.PackCount(repoPath).ShouldBe(1); // 1 unique chunk → 1 pack
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
