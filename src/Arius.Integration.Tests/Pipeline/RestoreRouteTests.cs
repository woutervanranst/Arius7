using Arius.Core.Features.RestoreCommand;
using Arius.Tests.Shared.Fixtures;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Tests for the restore pipeline's file route logic (step 3).
/// Covers all four route cases: New, SkipIdentical, Overwrite, KeepLocalDiffers.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RestoreRouteTests(AzuriteFixture azurite)
{
    // ── KeepLocalDiffers: file exists, hash differs, no --overwrite → do NOT restore ──

    [Test]
    public async Task Restore_LocalDiffers_NoOverwrite_DoesNotOverwriteFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a small file
        var originalContent = new byte[] { 1, 2, 3, 4, 5 };
        var relativePath = RelativePath.Parse("test.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, originalContent, CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a DIFFERENT file at the restore path (simulating local modification)
        var localContent = new byte[] { 99, 98, 97, 96, 95 };
        await fix.RestoreFileSystem.WriteAllBytesAsync(relativePath, localContent, CancellationToken.None);

        // Restore WITHOUT --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // The local file should NOT have been overwritten
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(localContent);
        restoreResult.FilesRestored.ShouldBe(0);
        restoreResult.FilesSkipped.ShouldBe(1, "KeepLocalDiffers files should be counted as skipped");
    }

    [Test]
    public async Task Restore_LocalDiffers_NoOverwrite_PublishesKeepLocalDiffersEvent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a small file
        var relativePath = RelativePath.Parse("test.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, [1, 2, 3, 4, 5], CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a DIFFERENT file at the restore path
        await fix.RestoreFileSystem.WriteAllBytesAsync(relativePath, [99, 98, 97], CancellationToken.None);

        // Clear any previous mediator calls
        fix.Mediator.ClearReceivedCalls();

        // Restore WITHOUT --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

        // Verify FileRoutedEvent with KeepLocalDiffers was published
        await fix.Mediator.Received().Publish(
            Arg.Is<FileRoutedEvent>(e =>
                e.RelativePath == relativePath &&
                e.Route == RestoreRoute.KeepLocalDiffers),
            Arg.Any<CancellationToken>());

        // Verify exactly 1 FileSkippedEvent was published with the file's size
        await fix.Mediator.Received(1).Publish(
            Arg.Is<FileSkippedEvent>(e => e.RelativePath == relativePath && e.FileSize > 0),
            Arg.Any<CancellationToken>());
    }

    // ── SkipIdentical: file exists, hash matches → skip ──

    [Test]
    public async Task Restore_LocalIdentical_SkipsFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a file
        var content = new byte[] { 10, 20, 30, 40, 50 };
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("same.txt"), content, CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Restore once to get the correct file in place
        var r1 = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = true,
        });
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        r1.FilesRestored.ShouldBe(1);

        // Clear mediator, restore again without overwrite → should skip
        fix.Mediator.ClearReceivedCalls();
        var r2 = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
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
        var relativePath = RelativePath.Parse("new.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Restore to empty dir (no local file) → should restore
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = false,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(content);
    }

    // ── Overwrite: file exists, hash differs, --overwrite set → restore ──

    [Test]
    public async Task Restore_LocalDiffers_WithOverwrite_OverwritesFile()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var originalContent = new byte[] { 1, 2, 3, 4, 5 };
        var relativePath = RelativePath.Parse("overwrite.txt");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, originalContent, CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Place a different file at the restore path
        await fix.RestoreFileSystem.WriteAllBytesAsync(relativePath, [99, 98, 97], CancellationToken.None);

        // Restore WITH --overwrite
        var restoreResult = await fix.RestoreAsync(new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = true,
        });

        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);
        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(originalContent);
    }
}
