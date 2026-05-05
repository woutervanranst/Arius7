using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies restore handlers with enriched FileSize parameter update all fields.
/// </summary>
public class RestoreHandlerNewSignatureTests
{
    [Test]
    public async Task FileRestoredHandler_UpdatesCounterBytesAndQueue()
    {
        var state   = new ProgressState();
        var handler = new FileRestoredHandler(state);

        await handler.Handle(new FileRestoredEvent(PathOf("a/b.txt"), 4096L), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(4096L);
        state.RecentRestoreEvents.Count.ShouldBe(1);
        state.RecentRestoreEvents.First().Skipped.ShouldBeFalse();
    }

    [Test]
    public async Task FileSkippedHandler_UpdatesCounterBytesAndQueue()
    {
        var state   = new ProgressState();
        var handler = new FileSkippedHandler(state);

        await handler.Handle(new FileSkippedEvent(PathOf("c/d.txt"), 2048L), CancellationToken.None);

        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(2048L);
        state.RecentRestoreEvents.Count.ShouldBe(1);
        state.RecentRestoreEvents.First().Skipped.ShouldBeTrue();
    }

    [Test]
    public async Task RehydrationStartedHandler_SetsBothFields()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStartedHandler(state);

        await handler.Handle(new RehydrationStartedEvent(3, 5_242_880L), CancellationToken.None);

        state.RehydrationChunkCount.ShouldBe(3);
        state.RehydrationTotalBytes.ShouldBe(5_242_880L);
    }
}
