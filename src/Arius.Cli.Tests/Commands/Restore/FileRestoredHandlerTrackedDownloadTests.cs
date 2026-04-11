using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies updated <see cref="FileRestoredHandler"/> removes TrackedDownload for large file downloads.
/// </summary>
public class FileRestoredHandlerTrackedDownloadTests
{
    [Test]
    public async Task FileRestoredHandler_RemovesLargeFileTrackedDownload()
    {
        var state = new ProgressState();
        // Key is RelativePath for large files (matching production behavior)
        var td = new TrackedDownload("videos/movie.mp4", DownloadKind.LargeFile, "videos/movie.mp4", 100_000_000, 200_000_000);
        state.TrackedDownloads.TryAdd("videos/movie.mp4", td);

        var handler = new FileRestoredHandler(state);
        await handler.Handle(new FileRestoredEvent("videos/movie.mp4", 200_000_000L), CancellationToken.None);

        state.TrackedDownloads.ContainsKey("videos/movie.mp4").ShouldBeFalse("Large file TrackedDownload should be removed");
        state.RestoreBytesDownloaded.ShouldBe(100_000_000L, "Should add CompressedSize to RestoreBytesDownloaded");
    }

    [Test]
    public async Task FileRestoredHandler_DoesNotRemoveTarBundleTrackedDownload()
    {
        // Tar bundle downloads are removed by ChunkDownloadCompletedHandler, not FileRestoredHandler
        var state = new ProgressState();
        var td = new TrackedDownload("tar_hash", DownloadKind.TarBundle, "TAR bundle (3 files, 500 KB)", 5_000_000, 500_000);
        state.TrackedDownloads.TryAdd("tar_hash", td);

        var handler = new FileRestoredHandler(state);
        // This file is from inside the tar bundle — handler should not remove the tar's TrackedDownload
        await handler.Handle(new FileRestoredEvent("docs/readme.txt", 1024L), CancellationToken.None);

        state.TrackedDownloads.ContainsKey("tar_hash").ShouldBeTrue("Tar TrackedDownload should NOT be removed by FileRestoredHandler");
        state.RestoreBytesDownloaded.ShouldBe(0L, "No compressed bytes should be added for tar bundle files");
    }
}
