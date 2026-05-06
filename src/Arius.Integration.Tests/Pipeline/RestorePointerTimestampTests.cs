using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Tests that restored files and their .pointer.arius files have correct timestamps.
/// Split into two tests to separately cover:
///   - RestoreTarBundleAsync  (small files &lt; SmallFileThreshold, bundled into tar)
///   - RestoreLargeFileAsync  (large files &gt;= SmallFileThreshold, stored as individual chunks)
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RestorePointerTimestampTests(AzuriteFixture azurite)
{
    /// <summary>
    /// Small files in nested directories go through the tar-bundle restore path
    /// (RestoreTarBundleAsync). Verifies that both the restored binary and its
    /// .pointer.arius file inherit the original Created/Modified timestamps.
    /// </summary>
    [Test]
    public async Task TarBundlePath_PointerTimestamps_MatchBinary()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // ── Arrange: multiple small files in nested directories ───────────
        var smallFiles = new (RelativePath RelPath, byte[] Content, DateTime Created, DateTime Modified)[]
        {
            (PathOf("docs/readme.txt"),       "hello world"u8.ToArray(),
             new(2023, 3, 10, 8, 0, 0, DateTimeKind.Utc),  new(2024, 5, 20, 16, 30, 0, DateTimeKind.Utc)),

            (PathOf("docs/notes/meeting.md"), "# Meeting notes\n- item 1\n- item 2"u8.ToArray(),
             new(2022, 11, 1, 12, 0, 0, DateTimeKind.Utc), new(2023, 7, 15, 9, 45, 0, DateTimeKind.Utc)),

            (PathOf("src/lib/util.cs"),       "namespace Util { class C {} }"u8.ToArray(),
             new(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc),   new(2025, 2, 28, 23, 59, 0, DateTimeKind.Utc)),
        };

        foreach (var (relPath, content, created, modified) in smallFiles)
        {
            var fullPath = fix.WriteFile(relPath, content);
            File.SetCreationTimeUtc(fullPath, created);
            File.SetLastWriteTimeUtc(fullPath, modified);
        }

        // ── Act ───────────────────────────────────────────────────────────
        var archiveResult = await fix.ArchiveAsync(new ArchiveCommandOptions
        {
            RootDirectory      = fix.LocalRoot,
            UploadTier         = BlobTier.Hot,
            SmallFileThreshold = 1024 * 1024,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
        });
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(3);

        // ── Assert ────────────────────────────────────────────────────────
        foreach (var (relPath, _, expectedCreated, expectedModified) in smallFiles)
        {
            var restoredPath = fix.RestoreRoot / relPath;
            var pointerPath = fix.RestoreRoot / relPath.ToPointerFilePath();

            restoredPath.ExistsFile.ShouldBeTrue($"Binary should exist: {relPath}");
            pointerPath.ExistsFile.ShouldBeTrue($"Pointer should exist: {relPath}");

            // Binary timestamps should match the originals
            // Note: CreationTimeUtc is not reliably settable on Linux (ext4 has no birth time),
            // so only assert it on Windows/macOS.
            if (!OperatingSystem.IsLinux())
            {
                restoredPath.CreationTimeUtc.ShouldBe(expectedCreated,
                    $"Binary CreationTimeUtc for {relPath}");
            }
            restoredPath.LastWriteTimeUtc.ShouldBe(expectedModified,
                $"Binary LastWriteTimeUtc for {relPath}");

            // Pointer timestamps should match the binary
            if (!OperatingSystem.IsLinux())
            {
                pointerPath.CreationTimeUtc.ShouldBe(expectedCreated,
                    $"Pointer CreationTimeUtc should match binary for {relPath}");
            }
            pointerPath.LastWriteTimeUtc.ShouldBe(expectedModified,
                $"Pointer LastWriteTimeUtc should match binary for {relPath}");
        }
    }

    /// <summary>
    /// A large file (>= SmallFileThreshold) goes through the single-chunk restore
    /// path (RestoreLargeFileAsync). This path streams download → decrypt → gunzip
    /// → FileStream; timestamps must be set AFTER the FileStream is closed,
    /// otherwise stream disposal overwrites LastWriteTimeUtc.
    /// Verifies both the restored binary and its .pointer.arius file timestamps.
    /// </summary>
    [Test]
    public async Task LargeFilePath_PointerTimestamps_MatchBinary()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // ── Arrange: a single file just over the 1 MB threshold ───────────
        var relPath  = PathOf("data/assets/bigfile.bin");
        var content  = new byte[1024 * 1024 + 512]; // just over 1 MB
        Random.Shared.NextBytes(content);

        var expectedCreated  = new DateTime(2021, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var expectedModified = new DateTime(2024, 12, 25, 18, 0, 0, DateTimeKind.Utc);

        var sourcePath = fix.WriteFile(relPath, content);
        File.SetCreationTimeUtc(sourcePath, expectedCreated);
        File.SetLastWriteTimeUtc(sourcePath, expectedModified);

        // ── Act ───────────────────────────────────────────────────────────
        var archiveResult = await fix.ArchiveAsync(new ArchiveCommandOptions
        {
            RootDirectory      = fix.LocalRoot,
            UploadTier         = BlobTier.Hot,
            SmallFileThreshold = 1024 * 1024,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
        });
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        // ── Assert ────────────────────────────────────────────────────────
        var restoredPath = fix.RestoreRoot / relPath;
        var pointerPath = fix.RestoreRoot / relPath.ToPointerFilePath();

        restoredPath.ExistsFile.ShouldBeTrue("Binary should exist");
        pointerPath.ExistsFile.ShouldBeTrue("Pointer should exist");

        // Binary content should be correct
        restoredPath.ReadAllBytes().ShouldBe(content);

        // Binary timestamps should match the originals
        // Note: CreationTimeUtc is not reliably settable on Linux (ext4 has no birth time),
        // so only assert it on Windows/macOS.
        if (!OperatingSystem.IsLinux())
        {
            restoredPath.CreationTimeUtc.ShouldBe(expectedCreated,
                "Binary CreationTimeUtc");
        }
        restoredPath.LastWriteTimeUtc.ShouldBe(expectedModified,
            "Binary LastWriteTimeUtc");

        // Pointer timestamps should match the binary
        if (!OperatingSystem.IsLinux())
        {
            pointerPath.CreationTimeUtc.ShouldBe(expectedCreated,
                "Pointer CreationTimeUtc should match binary");
        }
        pointerPath.LastWriteTimeUtc.ShouldBe(expectedModified,
            "Pointer LastWriteTimeUtc should match binary");
    }
}
