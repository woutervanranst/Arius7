using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Arius.Core.Shared.Extensions;

internal static class ChannelReaderExtensions
{
    extension<T>(ChannelReader<T> reader)
    {
        /// <summary>
        /// Reads the channel to completion as a sequence of batches, each holding up to
        /// <paramref name="maxBatchSize"/> items.
        /// </summary>
        /// <remarks>
        /// Greedy, timer-free batching: it waits for at least one item, then drains everything currently
        /// buffered (up to <paramref name="maxBatchSize"/>) without awaiting per item. Under load — when the
        /// producer keeps the channel backed up — this yields full <paramref name="maxBatchSize"/> batches and
        /// amortizes the per-batch work (e.g. one SQLite transaction per batch). When the producer trickles or
        /// finishes, it yields whatever is available immediately rather than blocking to fill a batch, so it
        /// never holds items hostage. No maximum-interval timer is needed for this because the channel
        /// completes deterministically and there is no consumer-latency requirement; a time-bounded variant
        /// could be layered on if one ever arises.
        ///
        /// Each yielded batch is a fresh list owned by the caller. Enumeration ends when the channel completes
        /// and is fully drained; a faulted channel surfaces its exception, and <paramref name="cancellationToken"/>
        /// cancellation throws <see cref="OperationCanceledException"/>.
        /// </remarks>
        public async IAsyncEnumerable<IReadOnlyList<T>> ReadAllBatchesAsync(int maxBatchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = new List<T>(maxBatchSize);
                while (batch.Count < maxBatchSize && reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count > 0)
                    yield return batch;
            }
        }
    }
}