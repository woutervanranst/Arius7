using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies updated <see cref="ChunkResolutionCompleteHandler"/> sets byte totals from enriched event.
/// </summary>
public class ChunkResolutionCompleteHandlerByteTotalsTests
{
    [Test]
    public async Task ChunkResolutionCompleteHandler_SetsByteTotals()
    {
        var state = new ProgressState();
        var handler = new ChunkResolutionCompleteHandler(state);

        await handler.Handle(
            new ChunkResolutionCompleteEvent(10, 5, 5, TotalChunkBytes: 200_000_000),
            CancellationToken.None);

        state.RestoreTotalChunks.ShouldBe(10);
        state.LargeChunkCount.ShouldBe(5);
        state.TarChunkCount.ShouldBe(5);
        state.RestoreTotalChunkBytes.ShouldBe(200_000_000L);
    }
}
