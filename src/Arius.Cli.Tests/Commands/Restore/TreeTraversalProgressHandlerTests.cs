using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="TreeTraversalProgressHandler"/> updates <see cref="ProgressState.RestoreFilesDiscovered"/>.
/// </summary>
public class TreeTraversalProgressHandlerTests
{
    [Test]
    public async Task TreeTraversalProgressHandler_SetsFilesDiscovered()
    {
        var state   = new ProgressState();
        var handler = new TreeTraversalProgressHandler(state);

        await handler.Handle(new TreeTraversalProgressEvent(523), CancellationToken.None);

        state.RestoreFilesDiscovered.ShouldBe(523L);
    }

    [Test]
    public async Task TreeTraversalProgressHandler_UpdatesOnSubsequentEvents()
    {
        var state   = new ProgressState();
        var handler = new TreeTraversalProgressHandler(state);

        await handler.Handle(new TreeTraversalProgressEvent(100), CancellationToken.None);
        state.RestoreFilesDiscovered.ShouldBe(100L);

        await handler.Handle(new TreeTraversalProgressEvent(523), CancellationToken.None);
        state.RestoreFilesDiscovered.ShouldBe(523L);

        await handler.Handle(new TreeTraversalProgressEvent(1247), CancellationToken.None);
        state.RestoreFilesDiscovered.ShouldBe(1247L);
    }
}
