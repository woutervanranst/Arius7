using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Shouldly;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="ChunkDownloadCompletedHandler"/> removes tracked download and increments bytes.
/// </summary>
public class ChunkDownloadCompletedHandlerTests
{
    [Test]
    public async Task ChunkDownloadCompletedHandler_RemovesTrackedDownloadAndIncrementsBytesDownloaded()
    {
        var state = new ProgressState();
        var td = new TrackedDownload("tar_hash", DownloadKind.TarBundle, "TAR bundle (5 files, 1.2 MB)", 8_000_000, 1_200_000);
        state.TrackedDownloads.TryAdd("tar_hash", td);

        var handler = new ChunkDownloadCompletedHandler(state);
        await handler.Handle(new ChunkDownloadCompletedEvent("tar_hash", 5, 8_000_000), CancellationToken.None);

        state.TrackedDownloads.ContainsKey("tar_hash").ShouldBeFalse();
        state.RestoreBytesDownloaded.ShouldBe(8_000_000L);
    }

    [Test]
    public async Task ChunkDownloadCompletedHandler_AccumulatesAcrossMultipleChunks()
    {
        var state = new ProgressState();
        state.TrackedDownloads.TryAdd("h1", new TrackedDownload("h1", DownloadKind.TarBundle, "TAR 1", 5_000_000, 1_000_000));
        state.TrackedDownloads.TryAdd("h2", new TrackedDownload("h2", DownloadKind.TarBundle, "TAR 2", 3_000_000, 800_000));

        var handler = new ChunkDownloadCompletedHandler(state);
        await handler.Handle(new ChunkDownloadCompletedEvent("h1", 3, 5_000_000), CancellationToken.None);
        await handler.Handle(new ChunkDownloadCompletedEvent("h2", 2, 3_000_000), CancellationToken.None);

        state.TrackedDownloads.Count.ShouldBe(0);
        state.RestoreBytesDownloaded.ShouldBe(8_000_000L);
    }
}
