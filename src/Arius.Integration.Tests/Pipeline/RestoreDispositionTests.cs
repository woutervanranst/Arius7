using Arius.Core.Restore;
using Arius.Integration.Tests.Storage;
using NSubstitute;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Tests for the restore pipeline's file disposition logic (step 3).
/// Covers all four disposition cases: New, SkipIdentical, Overwrite, KeepLocalDiffers.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RestoreDispositionTests(AzuriteFixture azurite)
{
    // ── KeepLocalDiffers: file exists, hash differs, no --overwrite → do NOT restore ──

    [Test]
    public async Task Restore_LocalDiffers_NoOverwrite_DoesNotOverwriteFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a small file
        var originalContent = new byte[] { 1, 2, 3, 4, 5 };
        fix.WriteFile("test.txt", originalContent);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a DIFFERENT file at the restore path (simulating local modification)
        var localContent = new byte[] { 99, 98, 97, 96, 95 };
        var restoredPath = Path.Combine(fix.RestoreRoot, "test.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        File.WriteAllBytes(restoredPath, localContent);

        // Restore WITHOUT --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // The local file should NOT have been overwritten
        File.ReadAllBytes(restoredPath).ShouldBe(localContent);
        restoreResult.FilesRestored.ShouldBe(0);
        restoreResult.FilesSkipped.ShouldBe(1, "KeepLocalDiffers files should be counted as skipped");
    }

    [Test]
    public async Task Restore_LocalDiffers_NoOverwrite_PublishesKeepLocalDiffersEvent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a small file
        fix.WriteFile("test.txt", new byte[] { 1, 2, 3, 4, 5 });
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a DIFFERENT file at the restore path
        var restoredPath = Path.Combine(fix.RestoreRoot, "test.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        File.WriteAllBytes(restoredPath, new byte[] { 99, 98, 97 });

        // Clear any previous mediator calls
        fix.Mediator.ClearReceivedCalls();

        // Restore WITHOUT --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // Verify FileDispositionEvent with KeepLocalDiffers was published
        await fix.Mediator.Received().Publish(
            Arg.Is<FileDispositionEvent>(e =>
                e.RelativePath == "test.txt" &&
                e.Disposition == RestoreDisposition.KeepLocalDiffers),
            Arg.Any<CancellationToken>());
    }

    // ── SkipIdentical: file exists, hash matches → skip ──

    [Test]
    public async Task Restore_LocalIdentical_SkipsFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a file
        var content = new byte[] { 10, 20, 30, 40, 50 };
        fix.WriteFile("same.txt", content);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Restore once to get the correct file in place
        var r1 = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
        });
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        r1.FilesRestored.ShouldBe(1);

        // Clear mediator, restore again without overwrite → should skip
        fix.Mediator.ClearReceivedCalls();
        var r2 = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = false,
        });

        r2.Success.ShouldBeTrue(r2.ErrorMessage);
        r2.FilesSkipped.ShouldBe(1);
        r2.FilesRestored.ShouldBe(0);
    }

    // ── New: file does not exist locally → restore ──

    [Test]
    public async Task Restore_NewFile_RestoresSuccessfully()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = new byte[] { 1, 2, 3 };
        fix.WriteFile("new.txt", content);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Restore to empty dir (no local file) → should restore
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.ReadRestored("new.txt").ShouldBe(content);
    }

    // ── Overwrite: file exists, hash differs, --overwrite set → restore ──

    [Test]
    public async Task Restore_LocalDiffers_WithOverwrite_OverwritesFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var originalContent = new byte[] { 1, 2, 3, 4, 5 };
        fix.WriteFile("overwrite.txt", originalContent);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a different file at the restore path
        var restoredPath = Path.Combine(fix.RestoreRoot, "overwrite.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        File.WriteAllBytes(restoredPath, new byte[] { 99, 98, 97 });

        // Restore WITH --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.ReadRestored("overwrite.txt").ShouldBe(originalContent);
    }
}
