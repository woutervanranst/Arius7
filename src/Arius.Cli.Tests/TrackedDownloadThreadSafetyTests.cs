using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies thread safety of concurrent TrackedDownload add/update/remove from 4 workers.
/// </summary>
public class TrackedDownloadThreadSafetyTests
{
    [Test]
    public async Task ConcurrentAddUpdateRemove_NoDataRaces()
    {
        var state = new ProgressState();
        const int n = 1_000;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (i, ct) =>
            {
                var key = $"file_{i}.bin"; // Key is RelativePath for large files
                var td = new TrackedDownload(key, DownloadKind.LargeFile, $"file_{i}.bin", 1_000_000, 2_000_000);
                state.TrackedDownloads.TryAdd(key, td);

                // Simulate byte-level progress
                td.SetBytesDownloaded(500_000);
                td.SetBytesDownloaded(1_000_000);

                // Remove on completion
                state.TrackedDownloads.TryRemove(key, out _);
                state.AddRestoreBytesDownloaded(1_000_000);
                return ValueTask.CompletedTask;
            });

        state.TrackedDownloads.Count.ShouldBe(0, "All tracked downloads should be removed");
        state.RestoreBytesDownloaded.ShouldBe(n * 1_000_000L, "All bytes should be accounted for");
    }
}
