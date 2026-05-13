using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// End-to-end roundtrip integration tests: archive → restore → verify content.
/// All tests use Azurite (via Docker/TestContainers) with Hot tier to avoid rehydration.
///
/// Covers tasks 13.1 – 13.8 (roundtrip scenarios) and 14.1 – 14.10 (edge cases).
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RoundtripTests(AzuriteFixture azurite)
{
    // ════════════════════════════════════════════════════════════════════════════
    // Section 13 — Roundtrip Scenarios
    // ════════════════════════════════════════════════════════════════════════════

    // ── 13.1: Single large file ───────────────────────────────────────────────

    [Test]
    public async Task Archive_SingleLargeFile_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // 2 MB > default 1 MB threshold → large pipeline
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("big.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(original);
    }

    // ── 13.2: Single small file (tar-bundled) ─────────────────────────────────

    [Test]
    public async Task Archive_SingleSmallFile_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // 100 bytes < 1 MB threshold → tar pipeline
        var original = new byte[100];
        Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("small.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(original);
    }

    // ── 13.3: Mix of large and small files ───────────────────────────────────

    [Test]
    public async Task Archive_MixedFiles_RestoreFullSnapshot_AllVerified()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var largeContent = new byte[2 * 1024 * 1024];
        var small1       = new byte[512];
        var small2       = new byte[1024];
        Random.Shared.NextBytes(largeContent);
        Random.Shared.NextBytes(small1);
        Random.Shared.NextBytes(small2);

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("large.bin"), largeContent, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small1.txt"), small1, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("small2.txt"), small2, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(3);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(3);

        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("large.bin")).ShouldBe(largeContent);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small1.txt")).ShouldBe(small1);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("small2.txt")).ShouldBe(small2);
    }

    // ── 13.4: Encryption roundtrip ────────────────────────────────────────────

    [Test]
    public async Task Archive_WithEncryption_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: "s3cr3t-p@ss");

        var original = new byte[800];
        Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("encrypted.dat");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(original);
    }

    // ── 13.5: Nested directory structure ─────────────────────────────────────

    [Test]
    public async Task Archive_NestedDirectories_Restore_TreeAndFilesMatch()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var files = new[]
        {
            ("photos/2024/june/vacation.jpg",  100),
            ("photos/2024/june/sunset.jpg",    200),
            ("photos/2024/december/xmas.jpg",  150),
            ("docs/readme.txt",                50),
            ("root.txt",                       20),
        };

        var contents = new Dictionary<string, byte[]>();
        foreach (var (path, size) in files)
        {
            var bytes = new byte[size];
            Random.Shared.NextBytes(bytes);
            await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse(path), bytes, CancellationToken.None);
            contents[path] = bytes;
        }

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(files.Length);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(files.Length);

        foreach (var (path, expected) in contents)
            fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse(path)).ShouldBe(expected, $"File mismatch: {path}");
    }

    // ── 13.6: Incremental archive — multiple snapshots ────────────────────────

    [Test]
    public async Task Archive_Incremental_EachSnapshotVersion_CorrectContent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // ── Snapshot 1: file-a ────────────────────────────────────────────────
        var contentA = new byte[100]; Random.Shared.NextBytes(contentA);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("file-a.bin"), contentA, CancellationToken.None);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100); // ensure distinct timestamp

        // ── Snapshot 2: add file-b ────────────────────────────────────────────
        var contentB = new byte[200]; Random.Shared.NextBytes(contentB);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("file-b.bin"), contentB, CancellationToken.None);

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // ── Restore snapshot 1 → only file-a ──────────────────────────────────
        var restoreResult1 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = fix.RestoreDirectory.Resolve(RelativePath.Parse("v1")),
                Version       = snapshot1,
                Overwrite     = true,
            }), default);

        restoreResult1.Success.ShouldBeTrue(restoreResult1.ErrorMessage);
        restoreResult1.FilesRestored.ShouldBe(1);

        var v1Directory = LocalDirectory.Parse(fix.RestoreDirectory.Resolve(RelativePath.Parse("v1")));
        var v1FileSystem = new RelativeFileSystem(v1Directory);
        v1FileSystem.FileExists(RelativePath.Parse("file-a.bin")).ShouldBeTrue();
        v1FileSystem.FileExists(RelativePath.Parse("file-b.bin")).ShouldBeFalse();
        v1FileSystem.ReadAllBytes(RelativePath.Parse("file-a.bin")).ShouldBe(contentA);

        // ── Restore latest snapshot → both files ──────────────────────────────
        var v2Directory = LocalDirectory.Parse(fix.RestoreDirectory.Resolve(RelativePath.Parse("v2")));
        var v2FileSystem = new RelativeFileSystem(v2Directory);
        var restoreResult2 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = v2Directory.ToString(),
                Overwrite     = true,
            }), default);

        restoreResult2.Success.ShouldBeTrue(restoreResult2.ErrorMessage);
        restoreResult2.FilesRestored.ShouldBe(2);
        v2FileSystem.ReadAllBytes(RelativePath.Parse("file-a.bin")).ShouldBe(contentA);
        v2FileSystem.ReadAllBytes(RelativePath.Parse("file-b.bin")).ShouldBe(contentB);
    }

    [Test]
    public async Task Archive_UnchangedRepository_DoesNotCreateNewSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var relativePath = RelativePath.Parse("file.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, "stable"u8.ToArray(), CancellationToken.None);

        var first = await fix.ArchiveAsync();
        first.Success.ShouldBeTrue(first.ErrorMessage);

        var snapshotCountAfterFirst = await fix.BlobContainer.ListAsync(BlobPaths.SnapshotsPrefix).CountAsync();
        snapshotCountAfterFirst.ShouldBe(1);

        var second = await fix.ArchiveAsync();
        second.Success.ShouldBeTrue(second.ErrorMessage);

        var snapshotCountAfterSecond = await fix.BlobContainer.ListAsync(BlobPaths.SnapshotsPrefix).CountAsync();
        snapshotCountAfterSecond.ShouldBe(1);
        second.RootHash.ShouldBe(first.RootHash);
        second.SnapshotTime.ShouldBe(first.SnapshotTime);
    }

    [Test]
    public async Task Archive_WithExistingPointerFiles_DoesNotCreateNewSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var relativePath = RelativePath.Parse("file.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, "stable"u8.ToArray(), CancellationToken.None);

        var first = await fix.ArchiveAsync();
        first.Success.ShouldBeTrue(first.ErrorMessage);
        fix.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue();

        var second = await fix.ArchiveAsync();
        second.Success.ShouldBeTrue(second.ErrorMessage);

        var snapshotCountAfterSecond = await fix.BlobContainer.ListAsync(BlobPaths.SnapshotsPrefix).CountAsync();
        snapshotCountAfterSecond.ShouldBe(1);
        second.RootHash.ShouldBe(first.RootHash);
        second.SnapshotTime.ShouldBe(first.SnapshotTime);
    }

    // ── 13.7: Deduplication — two identical files ─────────────────────────────

    [Test]
    public async Task Archive_TwoIdenticalFiles_SingleChunkUploaded_BothRestored()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[500]; Random.Shared.NextBytes(content);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("dir-a/photo.jpg"), content, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("dir-b/photo.jpg"), content, CancellationToken.None); // identical content

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(2);
        archiveResult.FilesDeduped.ShouldBe(1); // second file is deduplicated
        archiveResult.FilesUploaded.ShouldBe(1); // only one chunk uploaded

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("dir-a/photo.jpg")).ShouldBe(content);
        fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse("dir-b/photo.jpg")).ShouldBe(content);
    }

    // ── 13.8: Thin archive (--remove-local) ───────────────────────────────────

    [Test]
    public async Task Archive_RemoveLocal_ThenArchivePointers_Restore_Verify()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[300]; Random.Shared.NextBytes(content);
        var relativePath = RelativePath.Parse("data.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);

        // First archive with --remove-local
        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fix.LocalRoot,
                UploadTier    = BlobTier.Hot,
                RemoveLocal   = true,
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Binary should be gone, pointer should exist
        fix.LocalFileSystem.FileExists(relativePath).ShouldBeFalse();
        fix.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeTrue();

        // Second archive: only pointer file present (thin archive)
        var r2 = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fix.LocalRoot,
                UploadTier    = BlobTier.Hot,
            }), default);

        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore and verify
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(content);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Section 14 — Edge Cases
    // ════════════════════════════════════════════════════════════════════════════

    // ── 14.1: Stale pointer (binary hash ≠ pointer hash) ─────────────────────

    [Test]
    public async Task Archive_StalePointer_PointerOverwritten_CorrectHashArchived()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive original content
        var original = new byte[500]; Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("file.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);
        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();

        // Overwrite the binary with new content (making pointer stale)
        var updated = new byte[500]; Random.Shared.NextBytes(updated);
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, updated, CancellationToken.None);

        // Archive again — should detect stale pointer, archive new content
        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore latest snapshot → should have updated content
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(updated);
    }

    // ── 14.3: File renamed between runs ──────────────────────────────────────

    [Test]
    public async Task Archive_FileRenamed_OldPathAbsent_NewPathPresent_SameChunk()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[400]; Random.Shared.NextBytes(content);
        var originalPath = RelativePath.Parse("original.bin");
        var renamedPath = RelativePath.Parse("renamed.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(originalPath, content, CancellationToken.None);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        r1.FilesUploaded.ShouldBe(1);

        await Task.Delay(1100);

        // Rename: delete original, create renamed
        fix.LocalFileSystem.DeleteFile(originalPath);
        fix.LocalFileSystem.DeleteFile(originalPath.ToPointerPath());
        await fix.LocalFileSystem.WriteAllBytesAsync(renamedPath, content, CancellationToken.None);

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);
        r2.FilesDeduped.ShouldBe(1); // same content → deduplicated

        // Restore latest → renamed.bin present, original.bin absent
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();

        fix.RestoreFileSystem.FileExists(renamedPath).ShouldBeTrue();
        fix.RestoreFileSystem.FileExists(originalPath).ShouldBeFalse();
        fix.RestoreFileSystem.ReadAllBytes(renamedPath).ShouldBe(content);
    }

    // ── 14.4: File deleted between runs ──────────────────────────────────────

    [Test]
    public async Task Archive_FileDeleted_AbsentFromNewSnapshot_PresentInOld()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var contentA = new byte[100]; Random.Shared.NextBytes(contentA);
        var contentB = new byte[200]; Random.Shared.NextBytes(contentB);
        var keepPath = RelativePath.Parse("keep.bin");
        var deletePath = RelativePath.Parse("delete.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(keepPath, contentA, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(deletePath, contentB, CancellationToken.None);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100);

        // Delete one file and its pointer
        fix.LocalFileSystem.DeleteFile(deletePath);
        fix.LocalFileSystem.DeleteFile(deletePath.ToPointerPath());

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue();

        // Restore latest: keep.bin only
        var latestDirectory = LocalDirectory.Parse(fix.RestoreDirectory.Resolve(RelativePath.Parse("latest")));
        var latestFileSystem = new RelativeFileSystem(latestDirectory);
        var rl = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions { RootDirectory = latestDirectory.ToString(), Overwrite = true }), default);
        rl.Success.ShouldBeTrue();
        rl.FilesRestored.ShouldBe(1);
        latestFileSystem.FileExists(RelativePath.Parse("keep.bin")).ShouldBeTrue();
        latestFileSystem.FileExists(RelativePath.Parse("delete.bin")).ShouldBeFalse();

        // Restore snapshot 1: both files
        var v1Directory = LocalDirectory.Parse(fix.RestoreDirectory.Resolve(RelativePath.Parse("v1")));
        var v1FileSystem = new RelativeFileSystem(v1Directory);
        var rv1 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions { RootDirectory = v1Directory.ToString(), Version = snapshot1, Overwrite = true }), default);
        rv1.Success.ShouldBeTrue();
        rv1.FilesRestored.ShouldBe(2);
        v1FileSystem.FileExists(RelativePath.Parse("delete.bin")).ShouldBeTrue();
    }

    // ── 14.5: Special characters in filenames ────────────────────────────────

    [Test]
    public async Task Archive_SpecialCharacters_Roundtrip()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var files = new[]
        {
            "file with spaces.txt",
            "unicode-Ünïcödé.txt",
            "dots.and.dots.txt",
            "brackets[test].txt",
        };

        var contents = new Dictionary<string, byte[]>();
        foreach (var name in files)
        {
            var bytes = new byte[50]; Random.Shared.NextBytes(bytes);
            await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse(name), bytes, CancellationToken.None);
            contents[name] = bytes;
        }

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(files.Length);

        foreach (var (name, expected) in contents)
            fix.RestoreFileSystem.ReadAllBytes(RelativePath.Parse(name)).ShouldBe(expected, $"Mismatch: {name}");
    }

    // ── 14.6: Empty file (0 bytes) roundtrip ─────────────────────────────────

    [Test]
    public async Task Archive_EmptyFile_Roundtrip()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var relativePath = RelativePath.Parse("empty.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, Array.Empty<byte>(), CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBeEmpty();
    }

    // ── 14.7: File exactly at threshold boundary ──────────────────────────────

    [Test]
    public async Task Archive_FileAtThresholdBoundary_RoutedToLargePipeline()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // threshold = 1 MB; file at exactly 1 MB = large pipeline
        var content = new byte[1024 * 1024];
        Random.Shared.NextBytes(content);
        var relativePath = RelativePath.Parse("boundary.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);

        // Use a threshold of exactly the file size so it routes to large
        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = fix.LocalRoot,
                UploadTier         = BlobTier.Hot,
                SmallFileThreshold = 1024 * 1024, // = file size → routes to large
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(content);
    }

    // ── 14.2: Pointer-only file with missing chunk ────────────────────────────

    [Test]
    public async Task Archive_PointerOnlyWithMissingChunk_FileExcludedFromSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive one real file so the snapshot is non-empty
        var keepContent = new byte[100]; Random.Shared.NextBytes(keepContent);
        var keepPath = RelativePath.Parse("keep.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(keepPath, keepContent, CancellationToken.None);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        r1.FilesScanned.ShouldBe(1);

        // Now manufacture an orphan pointer that references a hash that does NOT exist in the index.
        // Write a fake 64-char hex hash into a .pointer.arius file with no corresponding binary.
        var fakeHash = new string('a', 64); // valid hex but no chunk exists for this hash
        await fix.LocalFileSystem.WriteAllTextAsync(RelativePath.Parse("ghost.bin.pointer.arius"), fakeHash, CancellationToken.None);

        await Task.Delay(1100); // distinct snapshot timestamp

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore latest → ghost.bin should NOT be restored (its chunk is missing)
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // keep.bin is in snapshot; ghost.bin was excluded due to missing chunk
        fix.RestoreFileSystem.FileExists(keepPath).ShouldBeTrue();
        fix.RestoreFileSystem.FileExists(RelativePath.Parse("ghost.bin")).ShouldBeFalse();
    }

    // ── 14.8: Binary named something.pointer.arius (naming collision) ────────

    [Test]
    public async Task Archive_FileNamedPointerArius_TreatedAsPointer_WarnedIfInvalidHex()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Create a binary file whose name ends in .pointer.arius with non-hex content.
        // It will be treated as a pointer, content is invalid hex → warning logged, hash=null.
        // With no binary counterpart, it is a pointer-only with null hash → skipped.
        var binaryContent = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }; // not valid hex string
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("data.pointer.arius"), binaryContent, CancellationToken.None);

        // Also add a normal file so the archive run has something to produce a snapshot
        var keepContent = new byte[50]; Random.Shared.NextBytes(keepContent);
        var normalPath = RelativePath.Parse("normal.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(normalPath, keepContent, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // The collision file is treated as a (broken) pointer and skipped.
        // Only normal.txt should be in the snapshot.
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoreFileSystem.FileExists(normalPath).ShouldBeTrue();
        // data.pointer.arius is treated as a pointer — it is not restored as a regular file
        fix.RestoreFileSystem.FileExists(RelativePath.Parse("data.pointer.arius")).ShouldBeFalse();
    }

    // ── 14.9: --no-pointers: no pointer files created ─────────────────────────

    [Test]
    public async Task Archive_NoPointers_NoPointerFilesCreated()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var relativePath = RelativePath.Parse("data.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, [1, 2, 3], CancellationToken.None);

        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fix.LocalRoot,
                UploadTier    = BlobTier.Hot,
                NoPointers    = true,
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // No pointer file should have been created
        fix.LocalFileSystem.FileExists(relativePath.ToPointerPath()).ShouldBeFalse();
    }

    // ── 14.10: --remove-local + --no-pointers: should be rejected ────────────

    [Test]
    public async Task Archive_RemoveLocalAndNoPointers_IsRejected()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("data.bin"), [1, 2, 3], CancellationToken.None);

        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fix.LocalRoot,
                UploadTier    = BlobTier.Hot,
                RemoveLocal   = true,
                NoPointers    = true,
            }), default);

        archiveResult.Success.ShouldBeFalse();
        archiveResult.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Section 16 — Streaming pipeline tests (pipeline-streaming change)
    // ════════════════════════════════════════════════════════════════════════════

    // ── 16.1: Streaming upload chain produces correct chunk-size metadata ─────

    [Test]
    public async Task Archive_LargeFile_StreamingChain_ChunkSizeMetadataIsSet()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // 2 MB > default 1 MB threshold → large pipeline (streaming chain)
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("large.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        // Find the chunk blob and verify chunk-size metadata was set by the streaming chain
        var blobs = new List<RelativePath>();
        await foreach (var name in fix.BlobContainer.ListAsync(BlobPaths.ChunksPrefix))
            blobs.Add(name);
        blobs.Count.ShouldBe(1);

        var meta = await fix.BlobContainer.GetMetadataAsync(blobs[0]);
        meta.Exists.ShouldBeTrue();
        meta.Metadata.ContainsKey(BlobMetadataKeys.AriusType).ShouldBeTrue();
        meta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        meta.Metadata.ContainsKey(BlobMetadataKeys.ChunkSize).ShouldBeTrue();
        long.Parse(meta.Metadata[BlobMetadataKeys.ChunkSize]).ShouldBeGreaterThan(0);

        // Verify roundtrip integrity
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(original);
    }

    // ── 16.2: Streaming enumeration — pipeline processes all files ────────────

    [Test]
    public async Task Archive_StreamingEnumeration_AllFilesProcessed()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Write several small files to exercise the streaming enumeration path
        for (var i = 0; i < 10; i++)
        {
            var relativePath = RelativePath.Parse($"file{i:D2}.bin");
            var bytes = new byte[512];
            Random.Shared.NextBytes(bytes);
            await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, bytes, CancellationToken.None);
        }

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        // All 10 files must have been enumerated and uploaded (none skipped due to eager materialisation)
        archiveResult.FilesScanned.ShouldBe(10);
        archiveResult.FilesUploaded.ShouldBe(10);

        // Restore and verify all files present
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(10);
        for (var i = 0; i < 10; i++)
            fix.RestoreFileSystem.FileExists(RelativePath.Parse($"file{i:D2}.bin")).ShouldBeTrue();
    }
}
