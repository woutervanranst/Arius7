using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="ChunkDownloadCompletedHandler"/> removes tracked download and increments bytes.
/// </summary>
public class ChunkDownloadCompletedHandlerTests
{
    private static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

    [Test]
    public async Task ChunkDownloadCompletedHandler_RemovesTrackedDownloadAndIncrementsBytesDownloaded()
    {
        var state = new ProgressState();
        var chunkHash = Chunk('a').ToString();
        var td = new TrackedDownload(chunkHash, DownloadKind.TarBundle, "TAR bundle (5 files, 1.2 MB)", 8_000_000, 1_200_000);
        state.TrackedDownloads.TryAdd(chunkHash, td);

        var handler = new ChunkDownloadCompletedHandler(state);
        await handler.Handle(new ChunkDownloadCompletedEvent(Chunk('a'), 5, 8_000_000), CancellationToken.None);

        state.TrackedDownloads.ContainsKey(chunkHash).ShouldBeFalse();
        state.RestoreBytesDownloaded.ShouldBe(8_000_000L);
    }

    [Test]
    public async Task ChunkDownloadCompletedHandler_AccumulatesAcrossMultipleChunks()
    {
        var state = new ProgressState();
        var chunkHash1 = Chunk('b').ToString();
        var chunkHash2 = Chunk('c').ToString();
        state.TrackedDownloads.TryAdd(chunkHash1, new TrackedDownload(chunkHash1, DownloadKind.TarBundle, "TAR 1", 5_000_000, 1_000_000));
        state.TrackedDownloads.TryAdd(chunkHash2, new TrackedDownload(chunkHash2, DownloadKind.TarBundle, "TAR 2", 3_000_000, 800_000));

        var handler = new ChunkDownloadCompletedHandler(state);
        await handler.Handle(new ChunkDownloadCompletedEvent(Chunk('b'), 3, 5_000_000), CancellationToken.None);
        await handler.Handle(new ChunkDownloadCompletedEvent(Chunk('c'), 2, 3_000_000), CancellationToken.None);

        state.TrackedDownloads.Count.ShouldBe(0);
        state.RestoreBytesDownloaded.ShouldBe(8_000_000L);
    }
}
