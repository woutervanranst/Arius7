using Arius.Core.Shared.Extensions;
using System.Threading.Channels;

namespace Arius.Core.Tests.Shared.Extensions;

public class ChannelReaderExtensionsTests
{
    [Test]
    public async Task ReadAllBatchesAsync_EmptyCompletedChannel_YieldsNothing()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.Complete();

        var batches = await CollectAsync(channel.Reader, maxBatchSize: 4);

        batches.ShouldBeEmpty();
    }

    [Test]
    public async Task ReadAllBatchesAsync_FewerThanBatchSize_YieldsSingleBatchWithAllItems()
    {
        var channel = Channel.CreateUnbounded<int>();
        foreach (var i in Enumerable.Range(0, 3))
            channel.Writer.TryWrite(i);
        channel.Writer.Complete();

        var batches = await CollectAsync(channel.Reader, maxBatchSize: 10);

        batches.Count.ShouldBe(1);
        batches[0].ShouldBe([0, 1, 2]);
    }

    [Test]
    public async Task ReadAllBatchesAsync_PreloadedItems_ChunksByMaxBatchSizeAndPreservesOrder()
    {
        var channel = Channel.CreateUnbounded<int>();
        foreach (var i in Enumerable.Range(0, 10))
            channel.Writer.TryWrite(i);
        channel.Writer.Complete();

        var batches = await CollectAsync(channel.Reader, maxBatchSize: 4);

        // All items already buffered → greedy drain fills full batches, then a trailing partial.
        batches.Select(b => b.Count).ShouldBe([4, 4, 2]);
        batches.SelectMany(b => b).ShouldBe(Enumerable.Range(0, 10));
    }

    [Test]
    public async Task ReadAllBatchesAsync_ItemsArrivingOverTime_DrainsAndCompletes()
    {
        var channel = Channel.CreateUnbounded<int>();

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                channel.Writer.TryWrite(i);
                await Task.Yield();
            }
            channel.Writer.Complete();
        });

        var batches = await CollectAsync(channel.Reader, maxBatchSize: 2);
        await producer;

        // Order preserved and every item delivered exactly once, regardless of how it batched.
        batches.SelectMany(b => b).ShouldBe(Enumerable.Range(0, 5));
        batches.ShouldAllBe(b => b.Count <= 2);
    }

    [Test]
    public async Task ReadAllBatchesAsync_NonPositiveBatchSize_Throws()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.Complete();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await CollectAsync(channel.Reader, maxBatchSize: 0));
    }

    [Test]
    public async Task ReadAllBatchesAsync_Cancellation_Throws()
    {
        var channel = Channel.CreateUnbounded<int>(); // never completed → WaitToReadAsync blocks
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await CollectAsync(channel.Reader, maxBatchSize: 4, cts.Token));
    }

    private static async Task<List<IReadOnlyList<T>>> CollectAsync<T>(
        ChannelReader<T> reader, int maxBatchSize, CancellationToken cancellationToken = default)
    {
        var batches = new List<IReadOnlyList<T>>();
        await foreach (var batch in reader.ReadAllBatchesAsync(maxBatchSize, cancellationToken))
            batches.Add(batch);
        return batches;
    }
}
