namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies byte accumulators are updated alongside counters.
/// </summary>
public class RestoreByteAccumulatorTests
{
    [Test]
    public void IncrementFilesRestored_UpdatesCounterAndBytes()
    {
        var state = new ProgressState();

        state.IncrementFilesRestored(1000L);
        state.IncrementFilesRestored(2000L);

        state.FilesRestored.ShouldBe(2L);
        state.BytesRestored.ShouldBe(3000L);
    }

    [Test]
    public void IncrementFilesSkipped_UpdatesCounterAndBytes()
    {
        var state = new ProgressState();

        state.IncrementFilesSkipped(512L);
        state.IncrementFilesSkipped(512L);

        state.FilesSkipped.ShouldBe(2L);
        state.BytesSkipped.ShouldBe(1024L);
    }
}
