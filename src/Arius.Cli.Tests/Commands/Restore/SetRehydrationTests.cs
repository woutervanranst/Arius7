using Shouldly;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="ProgressState.SetRehydration"/> sets both chunk count and byte total.
/// </summary>
public class SetRehydrationTests
{
    [Test]
    public void SetRehydration_SetsBothFields()
    {
        var state = new ProgressState();

        state.SetRehydration(5, 10_485_760L);

        state.RehydrationChunkCount.ShouldBe(5);
        state.RehydrationTotalBytes.ShouldBe(10_485_760L);
    }
}
