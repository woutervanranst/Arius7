using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies that restore progress handlers update the correct
/// <see cref="ProgressState"/> counters.
/// </summary>
public class RestoreProgressHandlerTests
{
    [Test]
    public async Task RestoreStartedHandler_SetsRestoreTotalFiles()
    {
        var state   = new ProgressState();
        var handler = new RestoreStartedHandler(state);

        await handler.Handle(new RestoreStartedEvent(1000), CancellationToken.None);

        state.RestoreTotalFiles.ShouldBe(1000);
    }

    [Test]
    public async Task FileRestoredHandler_IncrementsFilesRestored()
    {
        var state   = new ProgressState();
        var handler = new FileRestoredHandler(state);

        await handler.Handle(new FileRestoredEvent("dir/file.txt", 500L), CancellationToken.None);

        state.FilesRestored.ShouldBe(1L);
        state.BytesRestored.ShouldBe(500L);
    }

    [Test]
    public async Task FileSkippedHandler_IncrementsFilesSkipped()
    {
        var state   = new ProgressState();
        var handler = new FileSkippedHandler(state);

        await handler.Handle(new FileSkippedEvent("dir/existing.txt", 300L), CancellationToken.None);

        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(300L);
    }

    [Test]
    public async Task RehydrationStartedHandler_SetsChunkCount()
    {
        var state   = new ProgressState();
        var handler = new RehydrationStartedHandler(state);

        await handler.Handle(new RehydrationStartedEvent(7, 1024 * 1024), CancellationToken.None);

        state.RehydrationChunkCount.ShouldBe(7);
        state.RehydrationTotalBytes.ShouldBe(1024L * 1024L);
    }
}
