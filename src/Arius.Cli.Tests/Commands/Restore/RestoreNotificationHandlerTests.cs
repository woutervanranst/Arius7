using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Shouldly;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies the new restore notification handlers correctly update ProgressState.
/// </summary>
public class RestoreNotificationHandlerTests
{
    [Test]
    public async Task SnapshotResolvedHandler_SetsTimestampAndRootHash()
    {
        var state   = new ProgressState();
        var handler = new SnapshotResolvedHandler(state);
        var ts      = new DateTimeOffset(2026, 3, 28, 14, 0, 0, TimeSpan.Zero);

        await handler.Handle(new SnapshotResolvedEvent(ts, "abc123", 9), CancellationToken.None);

        state.SnapshotTimestamp.ShouldBe(ts);
        state.SnapshotRootHash.ShouldBe("abc123");
    }

    [Test]
    public async Task TreeTraversalCompleteHandler_SetsFileCountAndSize()
    {
        var state   = new ProgressState();
        var handler = new TreeTraversalCompleteHandler(state);

        await handler.Handle(new TreeTraversalCompleteEvent(42, 7_000_000L), CancellationToken.None);

        state.RestoreTotalFiles.ShouldBe(42);
        state.RestoreTotalOriginalSize.ShouldBe(7_000_000L);
        state.TreeTraversalComplete.ShouldBeTrue();
    }

    [Test]
    public async Task FileDispositionHandler_IncrementsNew()
    {
        var state   = new ProgressState();
        var handler = new FileDispositionHandler(state);

        await handler.Handle(new FileDispositionEvent("a.txt", RestoreDisposition.New, 1024L), CancellationToken.None);

        state.DispositionNew.ShouldBe(1);
        state.DispositionSkipIdentical.ShouldBe(0);
    }

    [Test]
    public async Task FileDispositionHandler_IncrementsSkipIdentical()
    {
        var state   = new ProgressState();
        var handler = new FileDispositionHandler(state);

        await handler.Handle(new FileDispositionEvent("b.txt", RestoreDisposition.SkipIdentical, 512L), CancellationToken.None);

        state.DispositionSkipIdentical.ShouldBe(1);
    }

    [Test]
    public async Task FileDispositionHandler_IncrementsOverwrite()
    {
        var state   = new ProgressState();
        var handler = new FileDispositionHandler(state);

        await handler.Handle(new FileDispositionEvent("c.txt", RestoreDisposition.Overwrite, 2048L), CancellationToken.None);

        state.DispositionOverwrite.ShouldBe(1);
    }

    [Test]
    public async Task FileDispositionHandler_IncrementsKeepLocalDiffers()
    {
        var state   = new ProgressState();
        var handler = new FileDispositionHandler(state);

        await handler.Handle(new FileDispositionEvent("d.txt", RestoreDisposition.KeepLocalDiffers, 4096L), CancellationToken.None);

        state.DispositionKeepLocalDiffers.ShouldBe(1);
    }

    [Test]
    public async Task ChunkResolutionCompleteHandler_SetsAllCounts()
    {
        var state   = new ProgressState();
        var handler = new ChunkResolutionCompleteHandler(state);

        await handler.Handle(new ChunkResolutionCompleteEvent(10, 3, 7), CancellationToken.None);

        state.ChunkGroups.ShouldBe(10);
        state.LargeChunkCount.ShouldBe(3);
        state.TarChunkCount.ShouldBe(7);
    }

    [Test]
    public async Task RehydrationStatusHandler_SetsAllCounts()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStatusHandler(state);

        await handler.Handle(new RehydrationStatusEvent(5, 2, 3, 1), CancellationToken.None);

        state.ChunksAvailable.ShouldBe(5);
        state.ChunksRehydrated.ShouldBe(2);
        state.ChunksNeedingRehydration.ShouldBe(3);
        state.ChunksPending.ShouldBe(1);
    }
}
