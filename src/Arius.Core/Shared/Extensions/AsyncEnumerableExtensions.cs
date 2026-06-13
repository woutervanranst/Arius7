using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Arius.Core.Shared.Extensions;

internal static class AsyncEnumerableExtensions
{
    /// <summary>
    /// Runs <paramref name="predicate"/> over the source with up to <paramref name="maxDegreeOfParallelism"/>
    /// concurrent evaluations, yielding the items it keeps as they complete (order is not preserved).
    /// </summary>
    /// <remarks>
    /// A bounded parallel filter: the kept items flow through an internal channel bounded to
    /// <paramref name="maxDegreeOfParallelism"/>, so a slow consumer backpressures the workers rather than
    /// buffering without limit. A predicate fault completes the channel with that exception, which surfaces
    /// through enumeration. An internal linked token is cancelled when enumeration ends — including an early
    /// <c>break</c> or a downstream throw — so a worker parked on the bounded write can never deadlock.
    /// </remarks>
    public static async IAsyncEnumerable<T> WhereParallelAsync<T>(
        this IAsyncEnumerable<T> source,
        int maxDegreeOfParallelism,
        Func<T, CancellationToken, ValueTask<bool>> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);

        var output = Channel.CreateBounded<T>(new BoundedChannelOptions(maxDegreeOfParallelism)
        {
            SingleWriter = false,
            SingleReader = true
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    source,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cts.Token },
                    async (item, ct) =>
                    {
                        if (await predicate(item, ct))
                            await output.Writer.WriteAsync(item, ct);
                    });
                output.Writer.Complete();
            }
            catch (Exception ex)
            {
                output.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in output.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }
        finally
        {
            // Unblock a worker still parked on WriteAsync if the consumer abandoned us early, then observe
            // the producer so nothing faults unobserved (it always completes — faults go to Writer.Complete).
            await cts.CancelAsync();
            await producer;
        }
    }
}
