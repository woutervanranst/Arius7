namespace Arius.Cli.Tests;

/// <summary>
/// Verifies <see cref="ProgressState"/> is thread-safe under concurrent operations.
/// </summary>
public class ProgressStateThreadSafetyTests
{
    [Test]
    public async Task ConcurrentAddAndRemove_NoDataRaces()
    {
        var state = new ProgressState();
        const int n = 5_000;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                var path = $"file{i}.bin";
                var hash = $"hash{i:x8}";
                state.AddFile(path, i * 1024L);
                state.SetFileHashed(path, hash);
                state.RemoveFile(path);
                return ValueTask.CompletedTask;
            });

        // All removed — counter should be n (each SetFileHashed increments FilesHashed)
        state.FilesHashed.ShouldBe(n);
        state.TrackedFiles.Count.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentIncrements_FilesHashed_CorrectTotal()
    {
        var state = new ProgressState();
        const int n = 10_000;

        for (var i = 0; i < n; i++)
            state.AddFile($"file{i}", 100);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                state.SetFileHashed($"file{i}", $"hash{i:x8}");
                return ValueTask.CompletedTask;
            });

        state.FilesHashed.ShouldBe(n);
    }

    [Test]
    public async Task ConcurrentIncrements_FilesRestored_CorrectTotal()
    {
        var state = new ProgressState();
        const int n = 8_000;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, n),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (_, _) =>
            {
                state.IncrementFilesRestored(1024L);
                return ValueTask.CompletedTask;
            });

        state.FilesRestored.ShouldBe(n);
        state.BytesRestored.ShouldBe(n * 1024L);
    }

    [Test]
    public void TotalFiles_NullUntilScanComplete()
    {
        var state = new ProgressState();
        state.TotalFiles.ShouldBeNull();
        state.SetScanComplete(1523, 1_000_000L);
        state.TotalFiles.ShouldBe(1523L);
        state.TotalBytes.ShouldBe(1_000_000L);
        state.ScanComplete.ShouldBeTrue();
    }
}
