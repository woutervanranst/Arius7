using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Tests.Shared.Fixtures;
using NSubstitute;

namespace Arius.Core.Tests.Features.ArchiveCommand;

/// <summary>
/// End-to-end (in-memory) archive → restore tests proving that excluded files/folders never enter a
/// snapshot, and that a file backed up in a previous run disappears once excluded on a rerun.
/// </summary>
public class ArchiveExclusionTests
{
    private static Task<ArchiveResult> ArchiveAsync(RepositoryTestFixture fixture, FileExclusionOptions? exclusions = null) =>
        fixture.CreateArchiveHandler(exclusions).Handle(
            new Arius.Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = fixture.LocalDirectory.ToString(),
                UploadTier    = BlobTier.Cool,
            }),
            CancellationToken.None).AsTask();

    private static Task<RestoreResult> RestoreAsync(RepositoryTestFixture fixture, LocalDirectory destination, string? version = null) =>
        fixture.CreateRestoreHandler().Handle(
            new Arius.Core.Features.RestoreCommand.RestoreCommand(new RestoreOptions
            {
                RootDirectory = destination.ToString(),
                Overwrite     = true,
                Version       = version,
            }),
            CancellationToken.None).AsTask();

    [Test]
    public async Task Archive_ExcludedEntries_AbsentFromSnapshot()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync();

        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("keep.bin"), [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("@eaDir/thumb.jpg"), [4, 5], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("thumbs.db"), [6, 7], CancellationToken.None);

        var exclusions = new FileExclusionOptions
        {
            ExcludedDirectoryNames = ["@eaDir"],
            ExcludedFileNames      = ["thumbs.db"],
        };

        var archiveResult = await ArchiveAsync(fixture, exclusions);
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.OriginalSize.ShouldBe(3); // snapshot contains only keep.bin (3 bytes)

        // Two enumeration skips: the @eaDir directory (pruned) and the thumbs.db file.
        archiveResult.FilesSkipped.ShouldBe(2);
        await fixture.Mediator.Received(2).Publish(Arg.Any<EntrySkippedEvent>(), Arg.Any<CancellationToken>());

        var restoreResult = await RestoreAsync(fixture, fixture.RestoreDirectory);
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("keep.bin")).ShouldBeTrue();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("@eaDir/thumb.jpg")).ShouldBeFalse();
        fixture.RestoreFileSystem.FileExists(RelativePath.Parse("thumbs.db")).ShouldBeFalse();
    }

    [Test]
    public async Task Archive_PreviouslyArchivedFile_DisappearsWhenExcludedOnRerun()
    {
        await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync();

        var keep = RelativePath.Parse("keep.bin");
        var junk = RelativePath.Parse("thumbs.db");
        await fixture.LocalFileSystem.WriteAllBytesAsync(keep, [1, 2, 3], CancellationToken.None);
        await fixture.LocalFileSystem.WriteAllBytesAsync(junk, [4, 5, 6, 7], CancellationToken.None);

        // Run 1: no exclusions → both files archived.
        var r1 = await ArchiveAsync(fixture);
        r1.Success.ShouldBeTrue(r1.ErrorMessage);
        r1.OriginalSize.ShouldBe(7);
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100); // ensure a distinct snapshot timestamp

        // Run 2: thumbs.db now excluded → it must disappear from the new snapshot.
        var r2 = await ArchiveAsync(fixture, new FileExclusionOptions { ExcludedFileNames = ["thumbs.db"] });
        r2.Success.ShouldBeTrue(r2.ErrorMessage);
        r2.OriginalSize.ShouldBe(3); // only keep.bin remains

        // Restore latest → thumbs.db absent.
        var latestDirectory = fixture.RestoreDirectory / RelativePath.Parse("latest");
        var latestFileSystem = new RelativeFileSystem(latestDirectory);
        var rl = await RestoreAsync(fixture, latestDirectory);
        rl.Success.ShouldBeTrue(rl.ErrorMessage);
        latestFileSystem.FileExists(keep).ShouldBeTrue();
        latestFileSystem.FileExists(junk).ShouldBeFalse();

        // Restore snapshot 1 → thumbs.db still present in the old snapshot.
        var v1Directory = fixture.RestoreDirectory / RelativePath.Parse("v1");
        var v1FileSystem = new RelativeFileSystem(v1Directory);
        var rv1 = await RestoreAsync(fixture, v1Directory, snapshot1);
        rv1.Success.ShouldBeTrue(rv1.ErrorMessage);
        v1FileSystem.FileExists(keep).ShouldBeTrue();
        v1FileSystem.FileExists(junk).ShouldBeTrue();
    }
}
