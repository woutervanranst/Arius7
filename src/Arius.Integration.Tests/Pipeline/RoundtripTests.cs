using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Paths;
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
        fix.WriteFile(PathOf("big.bin"), original);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.ReadRestored(PathOf("big.bin")).ShouldBe(original);
    }

    // ── 13.2: Single small file (tar-bundled) ─────────────────────────────────

    [Test]
    public async Task Archive_SingleSmallFile_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // 100 bytes < 1 MB threshold → tar pipeline
        var original = new byte[100];
        Random.Shared.NextBytes(original);
        fix.WriteFile(PathOf("small.txt"), original);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.ReadRestored(PathOf("small.txt")).ShouldBe(original);
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

        fix.WriteFile(PathOf("large.bin"), largeContent);
        fix.WriteFile(PathOf("small1.txt"), small1);
        fix.WriteFile(PathOf("small2.txt"), small2);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(3);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(3);

        fix.ReadRestored(PathOf("large.bin")).ShouldBe(largeContent);
        fix.ReadRestored(PathOf("small1.txt")).ShouldBe(small1);
        fix.ReadRestored(PathOf("small2.txt")).ShouldBe(small2);
    }

    // ── 13.4: Encryption roundtrip ────────────────────────────────────────────

    [Test]
    public async Task Archive_WithEncryption_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: "s3cr3t-p@ss");

        var original = new byte[800];
        Random.Shared.NextBytes(original);
        fix.WriteFile(PathOf("encrypted.dat"), original);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        fix.ReadRestored(PathOf("encrypted.dat")).ShouldBe(original);
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
            fix.WriteFile(PathOf(path), bytes);
            contents[path] = bytes;
        }

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(files.Length);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(files.Length);

        foreach (var (path, expected) in contents)
            fix.ReadRestored(PathOf(path)).ShouldBe(expected, $"File mismatch: {path}");
    }

    // ── 13.6: Incremental archive — multiple snapshots ────────────────────────

    [Test]
    public async Task Archive_Incremental_EachSnapshotVersion_CorrectContent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // ── Snapshot 1: file-a ────────────────────────────────────────────────
        var contentA = new byte[100]; Random.Shared.NextBytes(contentA);
        fix.WriteFile(PathOf("file-a.bin"), contentA);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100); // ensure distinct timestamp

        // ── Snapshot 2: add file-b ────────────────────────────────────────────
        var contentB = new byte[200]; Random.Shared.NextBytes(contentB);
        fix.WriteFile(PathOf("file-b.bin"), contentB);

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // ── Restore snapshot 1 → only file-a ──────────────────────────────────
        var restoreResult1 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RootOf(fix.RestoreRoot + "/v1"),
                Version       = snapshot1,
                Overwrite     = true,
            }), default);

        restoreResult1.Success.ShouldBeTrue(restoreResult1.ErrorMessage);
        restoreResult1.FilesRestored.ShouldBe(1);

        var v1Dir = fix.RestoreRoot + "/v1";
        File.Exists(Path.Combine(v1Dir, "file-a.bin")).ShouldBeTrue();
        File.Exists(Path.Combine(v1Dir, "file-b.bin")).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(v1Dir, "file-a.bin")).ShouldBe(contentA);

        // ── Restore latest snapshot → both files ──────────────────────────────
        var v2Dir = fix.RestoreRoot + "/v2";
        var restoreResult2 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RootOf(v2Dir),
                Overwrite     = true,
            }), default);

        restoreResult2.Success.ShouldBeTrue(restoreResult2.ErrorMessage);
        restoreResult2.FilesRestored.ShouldBe(2);
        File.ReadAllBytes(Path.Combine(v2Dir, "file-a.bin")).ShouldBe(contentA);
        File.ReadAllBytes(Path.Combine(v2Dir, "file-b.bin")).ShouldBe(contentB);
    }

    [Test]
    public async Task Archive_UnchangedRepository_DoesNotCreateNewSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("file.bin"), "stable"u8.ToArray());

        var first = await fix.ArchiveAsync();
        first.Success.ShouldBeTrue(first.ErrorMessage);

        var snapshotCountAfterFirst = await fix.BlobContainer.ListAsync(BlobPaths.Snapshots).CountAsync();
        snapshotCountAfterFirst.ShouldBe(1);

        var second = await fix.ArchiveAsync();
        second.Success.ShouldBeTrue(second.ErrorMessage);

        var snapshotCountAfterSecond = await fix.BlobContainer.ListAsync(BlobPaths.Snapshots).CountAsync();
        snapshotCountAfterSecond.ShouldBe(1);
        second.RootHash.ShouldBe(first.RootHash);
        second.SnapshotTime.ShouldBe(first.SnapshotTime);
    }

    [Test]
    public async Task Archive_WithExistingPointerFiles_DoesNotCreateNewSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("file.bin"), "stable"u8.ToArray());

        var first = await fix.ArchiveAsync();
        first.Success.ShouldBeTrue(first.ErrorMessage);
        File.Exists(Path.Combine(fix.LocalRoot, "file.bin.pointer.arius")).ShouldBeTrue();

        var second = await fix.ArchiveAsync();
        second.Success.ShouldBeTrue(second.ErrorMessage);

        var snapshotCountAfterSecond = await fix.BlobContainer.ListAsync(BlobPaths.Snapshots).CountAsync();
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
        fix.WriteFile(PathOf("dir-a/photo.jpg"), content);
        fix.WriteFile(PathOf("dir-b/photo.jpg"), content); // identical content

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(2);
        archiveResult.FilesDeduped.ShouldBe(1); // second file is deduplicated
        archiveResult.FilesUploaded.ShouldBe(1); // only one chunk uploaded

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.ReadRestored(PathOf("dir-a/photo.jpg")).ShouldBe(content);
        fix.ReadRestored(PathOf("dir-b/photo.jpg")).ShouldBe(content);
    }

    // ── 13.8: Thin archive (--remove-local) ───────────────────────────────────

    [Test]
    public async Task Archive_RemoveLocal_ThenArchivePointers_Restore_Verify()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[300]; Random.Shared.NextBytes(content);
        fix.WriteFile(PathOf("data.bin"), content);

        // First archive with --remove-local
        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = RootOf(fix.LocalRoot),
                UploadTier    = BlobTier.Hot,
                RemoveLocal   = true,
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Binary should be gone, pointer should exist
        File.Exists(Path.Combine(fix.LocalRoot, "data.bin")).ShouldBeFalse();
        File.Exists(Path.Combine(fix.LocalRoot, "data.bin.pointer.arius")).ShouldBeTrue();

        // Second archive: only pointer file present (thin archive)
        var r2 = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = RootOf(fix.LocalRoot),
                UploadTier    = BlobTier.Hot,
            }), default);

        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore and verify
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.ReadRestored(PathOf("data.bin")).ShouldBe(content);
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
        fix.WriteFile(PathOf("file.bin"), original);
        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();

        // Overwrite the binary with new content (making pointer stale)
        var updated = new byte[500]; Random.Shared.NextBytes(updated);
        fix.WriteFile(PathOf("file.bin"), updated);

        // Archive again — should detect stale pointer, archive new content
        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore latest snapshot → should have updated content
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();
        fix.ReadRestored(PathOf("file.bin")).ShouldBe(updated);
    }

    // ── 14.3: File renamed between runs ──────────────────────────────────────

    [Test]
    public async Task Archive_FileRenamed_OldPathAbsent_NewPathPresent_SameChunk()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[400]; Random.Shared.NextBytes(content);
        fix.WriteFile(PathOf("original.bin"), content);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        r1.FilesUploaded.ShouldBe(1);

        await Task.Delay(1100);

        // Rename: delete original, create renamed
        File.Delete(Path.Combine(fix.LocalRoot, "original.bin"));
        File.Delete(Path.Combine(fix.LocalRoot, "original.bin.pointer.arius"));
        fix.WriteFile(PathOf("renamed.bin"), content);

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);
        r2.FilesDeduped.ShouldBe(1); // same content → deduplicated

        // Restore latest → renamed.bin present, original.bin absent
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();

        fix.RestoredExists(PathOf("renamed.bin")).ShouldBeTrue();
        fix.RestoredExists(PathOf("original.bin")).ShouldBeFalse();
        fix.ReadRestored(PathOf("renamed.bin")).ShouldBe(content);
    }

    // ── 14.4: File deleted between runs ──────────────────────────────────────

    [Test]
    public async Task Archive_FileDeleted_AbsentFromNewSnapshot_PresentInOld()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var contentA = new byte[100]; Random.Shared.NextBytes(contentA);
        var contentB = new byte[200]; Random.Shared.NextBytes(contentB);
        fix.WriteFile(PathOf("keep.bin"), contentA);
        fix.WriteFile(PathOf("delete.bin"), contentB);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100);

        // Delete one file and its pointer
        File.Delete(Path.Combine(fix.LocalRoot, "delete.bin"));
        File.Delete(Path.Combine(fix.LocalRoot, "delete.bin.pointer.arius"));

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue();

        // Restore latest: keep.bin only
        var latestDir = Path.Combine(fix.RestoreRoot, "latest");
        var rl = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions { RootDirectory = RootOf(latestDir), Overwrite = true }), default);
        rl.Success.ShouldBeTrue();
        rl.FilesRestored.ShouldBe(1);
        File.Exists(Path.Combine(latestDir, "keep.bin")).ShouldBeTrue();
        File.Exists(Path.Combine(latestDir, "delete.bin")).ShouldBeFalse();

        // Restore snapshot 1: both files
        var v1Dir = Path.Combine(fix.RestoreRoot, "v1");
        var rv1 = await fix.CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions { RootDirectory = RootOf(v1Dir), Version = snapshot1, Overwrite = true }), default);
        rv1.Success.ShouldBeTrue();
        rv1.FilesRestored.ShouldBe(2);
        File.Exists(Path.Combine(v1Dir, "delete.bin")).ShouldBeTrue();
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
            fix.WriteFile(PathOf(name), bytes);
            contents[name] = bytes;
        }

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(files.Length);

        foreach (var (name, expected) in contents)
            fix.ReadRestored(PathOf(name)).ShouldBe(expected, $"Mismatch: {name}");
    }

    // ── 14.6: Empty file (0 bytes) roundtrip ─────────────────────────────────

    [Test]
    public async Task Archive_EmptyFile_Roundtrip()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("empty.txt"), Array.Empty<byte>());

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesScanned.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.ReadRestored(PathOf("empty.txt")).ShouldBeEmpty();
    }

    // ── 14.7: File exactly at threshold boundary ──────────────────────────────

    [Test]
    public async Task Archive_FileAtThresholdBoundary_RoutedToLargePipeline()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // threshold = 1 MB; file at exactly 1 MB = large pipeline
        var content = new byte[1024 * 1024];
        Random.Shared.NextBytes(content);
        fix.WriteFile(PathOf("boundary.bin"), content);

        // Use a threshold of exactly the file size so it routes to large
        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory      = RootOf(fix.LocalRoot),
                UploadTier         = BlobTier.Hot,
                SmallFileThreshold = 1024 * 1024, // = file size → routes to large
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue();
        fix.ReadRestored(PathOf("boundary.bin")).ShouldBe(content);
    }

    // ── 14.2: Pointer-only file with missing chunk ────────────────────────────

    [Test]
    public async Task Archive_PointerOnlyWithMissingChunk_FileExcludedFromSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive one real file so the snapshot is non-empty
        var keepContent = new byte[100]; Random.Shared.NextBytes(keepContent);
        fix.WriteFile(PathOf("keep.bin"), keepContent);

        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        r1.FilesScanned.ShouldBe(1);

        // Now manufacture an orphan pointer that references a hash that does NOT exist in the index.
        // Write a fake 64-char hex hash into a .pointer.arius file with no corresponding binary.
        var fakeHash = new string('a', 64); // valid hex but no chunk exists for this hash
        var pointerPath = Path.Combine(fix.LocalRoot, "ghost.bin.pointer.arius");
        await File.WriteAllTextAsync(pointerPath, fakeHash);

        await Task.Delay(1100); // distinct snapshot timestamp

        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue(r2.ErrorMessage);

        // Restore latest → ghost.bin should NOT be restored (its chunk is missing)
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // keep.bin is in snapshot; ghost.bin was excluded due to missing chunk
        fix.RestoredExists(PathOf("keep.bin")).ShouldBeTrue();
        fix.RestoredExists(PathOf("ghost.bin")).ShouldBeFalse();
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
        fix.WriteFile(PathOf("data.pointer.arius"), binaryContent);

        // Also add a normal file so the archive run has something to produce a snapshot
        var keepContent = new byte[50]; Random.Shared.NextBytes(keepContent);
        fix.WriteFile(PathOf("normal.txt"), keepContent);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // The collision file is treated as a (broken) pointer and skipped.
        // Only normal.txt should be in the snapshot.
        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoredExists(PathOf("normal.txt")).ShouldBeTrue();
        // data.pointer.arius is treated as a pointer — it is not restored as a regular file
        fix.RestoredExists(PathOf("data.pointer.arius")).ShouldBeFalse();
    }

    // ── 14.9: --no-pointers: no pointer files created ─────────────────────────

    [Test]
    public async Task Archive_NoPointers_NoPointerFilesCreated()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("data.bin"), new byte[] { 1, 2, 3 });

        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = RootOf(fix.LocalRoot),
                UploadTier    = BlobTier.Hot,
                NoPointers    = true,
            }), default);

        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // No pointer file should have been created
        File.Exists(Path.Combine(fix.LocalRoot, "data.bin.pointer.arius")).ShouldBeFalse();
    }

    // ── 14.10: --remove-local + --no-pointers: should be rejected ────────────

    [Test]
    public async Task Archive_RemoveLocalAndNoPointers_IsRejected()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("data.bin"), new byte[] { 1, 2, 3 });

        var archiveResult = await fix.CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = RootOf(fix.LocalRoot),
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
        fix.WriteFile(PathOf("large.bin"), original);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        // Find the chunk blob and verify chunk-size metadata was set by the streaming chain
        var blobs = new List<string>();
        await foreach (var name in fix.BlobContainer.ListAsync("chunks/"))
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
        fix.ReadRestored(PathOf("large.bin")).ShouldBe(original);
    }

    // ── 16.2: Streaming enumeration — pipeline processes all files ────────────

    [Test]
    public async Task Archive_StreamingEnumeration_AllFilesProcessed()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Write several small files to exercise the streaming enumeration path
        for (var i = 0; i < 10; i++)
            fix.WriteRandomFile(PathOf($"file{i:D2}.bin"), sizeBytes: 512);

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
            fix.RestoredExists(PathOf($"file{i:D2}.bin")).ShouldBeTrue();
    }
}
