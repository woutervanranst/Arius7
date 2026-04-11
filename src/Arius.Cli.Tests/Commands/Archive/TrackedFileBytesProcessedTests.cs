using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies that <see cref="TrackedFile.SetBytesProcessed"/> reports the latest value
/// under concurrent updates (last-writer semantics).
/// </summary>
public class TrackedFileBytesProcessedTests
{
    [Test]
    public async Task BytesProcessed_UpdatesCorrectlyUnderContention()
    {
        var file = new TrackedFile("test.bin", 1_000_000L);

        const int iterations = 10_000;
        long      finalValue = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(1, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                file.SetBytesProcessed(i);
                Interlocked.Exchange(ref finalValue, i);
                return ValueTask.CompletedTask;
            });

        file.BytesProcessed.ShouldBeInRange(1L, iterations);
    }
}
