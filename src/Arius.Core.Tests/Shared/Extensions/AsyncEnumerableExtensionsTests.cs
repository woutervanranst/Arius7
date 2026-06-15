using Arius.Core.Shared.Extensions;

namespace Arius.Core.Tests.Shared.Extensions;

public class AsyncEnumerableExtensionsTests
{
    [Test]
    public async Task WhereParallelAsync_KeepsMatching_DropsRest()
    {
        var kept = await CollectAsync(
            Range(0, 10).WhereParallelAsync(4, (i, _) => ValueTask.FromResult(i % 2 == 0)));

        // Order is not preserved (yielded as completed), so compare as a set.
        kept.Order().ShouldBe([0, 2, 4, 6, 8]);
    }

    [Test]
    public async Task WhereParallelAsync_AllTrue_KeepsEverything()
    {
        var kept = await CollectAsync(
            Range(0, 5).WhereParallelAsync(3, (_, _) => ValueTask.FromResult(true)));

        kept.Order().ShouldBe([0, 1, 2, 3, 4]);
    }

    [Test]
    public async Task WhereParallelAsync_PredicateThrows_SurfacesThroughEnumeration()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await CollectAsync(Range(0, 10).WhereParallelAsync(4, (i, _) =>
                i == 5
                    ? throw new InvalidOperationException("boom")
                    : ValueTask.FromResult(true))));
    }

    [Test]
    public async Task WhereParallelAsync_NonPositiveDegree_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await CollectAsync(Range(0, 3).WhereParallelAsync(0, (_, _) => ValueTask.FromResult(true))));
    }

    [Test]
    public async Task WhereParallelAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await CollectAsync(Range(0, 100).WhereParallelAsync(4, (_, _) => ValueTask.FromResult(true), cts.Token)));
    }

    [Test]
    public async Task WhereParallelAsync_NeverExceedsMaxDegreeOfParallelism()
    {
        var current     = 0;
        var observedMax  = 0;
        var gate        = new object();

        var kept = await CollectAsync(Range(0, 50).WhereParallelAsync(4, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            lock (gate) observedMax = Math.Max(observedMax, now);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref current);
            return true;
        }));

        kept.Count.ShouldBe(50);
        observedMax.ShouldBeLessThanOrEqualTo(4);
    }

    private static async IAsyncEnumerable<int> Range(int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }
}
