using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="TrackedDownload"/> lifecycle: add, update bytes, remove on completion.
/// Covers large file (removed by FileRestoredHandler) and tar bundle (removed by ChunkDownloadCompletedHandler).
/// </summary>
public class TrackedDownloadLifecycleTests
{
    [Test]
    public async Task LargeFile_TrackedDownload_AddUpdateRemove()
    {
        var state = new ProgressState();

        // Simulate CreateDownloadProgress adding a TrackedDownload for a large file
        // Key is RelativePath (the identifier passed from RestoreCommandHandler for large files)
        var td = new TrackedDownload("photos/sunset.jpg", DownloadKind.LargeFile, "photos/sunset.jpg", compressedSize: 25_400_000, originalSize: 50_000_000);
        state.TrackedDownloads.TryAdd("photos/sunset.jpg", td);

        state.TrackedDownloads.Count.ShouldBe(1);
        state.TrackedDownloads["photos/sunset.jpg"].DisplayName.ShouldBe("photos/sunset.jpg");
        state.TrackedDownloads["photos/sunset.jpg"].Kind.ShouldBe(DownloadKind.LargeFile);

        // Simulate byte-level progress updates
        td.SetBytesDownloaded(12_300_000);
        td.BytesDownloaded.ShouldBe(12_300_000L);

        td.SetBytesDownloaded(25_400_000);
        td.BytesDownloaded.ShouldBe(25_400_000L);

        // FileRestoredHandler should remove the TrackedDownload by direct key lookup on RelativePath
        var handler = new FileRestoredHandler(state);
        await handler.Handle(new FileRestoredEvent(PathOf("photos/sunset.jpg"), 50_000_000L), CancellationToken.None);

        state.TrackedDownloads.ContainsKey("photos/sunset.jpg").ShouldBeFalse("TrackedDownload should be removed after FileRestoredEvent");
        state.RestoreBytesDownloaded.ShouldBe(25_400_000L, "RestoreBytesDownloaded should be incremented by CompressedSize");
        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(50_000_000L);
    }

    [Test]
    public async Task TarBundle_TrackedDownload_AddUpdateRemove()
    {
        var state = new ProgressState();
        var chunkHash = FakeChunkHash('a').ToString();

        // Simulate CreateDownloadProgress adding a TrackedDownload for a tar bundle
        var td = new TrackedDownload(chunkHash, DownloadKind.TarBundle, "TAR bundle (3 files, 847 KB)", compressedSize: 15_200_000, originalSize: 847_000);
        state.TrackedDownloads.TryAdd(chunkHash, td);

        state.TrackedDownloads.Count.ShouldBe(1);
        state.TrackedDownloads[chunkHash].Kind.ShouldBe(DownloadKind.TarBundle);

        // Simulate byte-level progress
        td.SetBytesDownloaded(4_800_000);
        td.BytesDownloaded.ShouldBe(4_800_000L);

        // ChunkDownloadCompletedHandler should remove the TrackedDownload
        var handler = new ChunkDownloadCompletedHandler(state);
        await handler.Handle(new ChunkDownloadCompletedEvent(FakeChunkHash('a'), 3, 15_200_000), CancellationToken.None);

        state.TrackedDownloads.ContainsKey(chunkHash).ShouldBeFalse("TrackedDownload should be removed after ChunkDownloadCompletedEvent");
        state.RestoreBytesDownloaded.ShouldBe(15_200_000L, "RestoreBytesDownloaded should be incremented by CompressedSize");
    }

    [Test]
    public void TrackedDownload_BytesDownloaded_InterlockedUpdate()
    {
        var td = new TrackedDownload("key1", DownloadKind.LargeFile, "file.bin", 1_000_000, 2_000_000);

        td.BytesDownloaded.ShouldBe(0L);
        td.SetBytesDownloaded(500_000);
        td.BytesDownloaded.ShouldBe(500_000L);
        td.SetBytesDownloaded(1_000_000);
        td.BytesDownloaded.ShouldBe(1_000_000L);
    }

    [Test]
    public async Task FileRestoredHandler_NoTrackedDownload_StillUpdatesCounters()
    {
        // When no TrackedDownload exists (e.g. file from tar bundle), handler should still work
        var state   = new ProgressState();
        var handler = new FileRestoredHandler(state);

        await handler.Handle(new FileRestoredEvent(PathOf("some/file.txt"), 1024L), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(1024L);
        // No TrackedDownload removal expected, RestoreBytesDownloaded stays at 0
        state.RestoreBytesDownloaded.ShouldBe(0L);
    }
}
