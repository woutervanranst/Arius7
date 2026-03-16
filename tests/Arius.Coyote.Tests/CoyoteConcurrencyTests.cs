using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Xunit;

namespace Arius.Coyote.Tests;

/// <summary>
/// 9.2 — Coyote explores 1000 interleavings of 4 workers claiming the same blob hash.
///        In every interleaving, exactly 1 blob must reach the "packing channel".
///
/// 9.3 — Coyote explores 500 interleavings of a bounded producer-consumer pipeline.
///        No deadlock should be detected.
///
/// 9.4 — Coyote explores pipeline completion: all events must be delivered and the
///        channel writer must be properly completed in every interleaving.
/// </summary>
public class CoyoteConcurrencyTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 9.2  Dedup gate — exactly 1 blob wins across all interleavings
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DedupGate_4WorkersSameHash_ExactlyOneBlobWinsInAllInterleavings()
    {
        var config = Configuration.Create()
            .WithTestingIterations(1000)
            .WithMaxSchedulingSteps(500);

        var engine = TestingEngine.Create(config, DedupGate_ConcurrentBody);
        engine.Run();

        var report = engine.TestReport;
        Assert.True(report.NumOfFoundBugs == 0,
            $"Coyote found {report.NumOfFoundBugs} bug(s) in the dedup gate:\n{report.BugReports.FirstOrDefault()}");
    }

    private static async Task DedupGate_ConcurrentBody()
    {
        var dedupDict = new ConcurrentDictionary<string, int>();
        // Simulate packing channel: bounded so we can assert exactly 1 item
        var packChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
        {
            SingleWriter = false,
            SingleReader = false,
        });

        const string hash = "shared-blob-hash-abc123";
        const int workers = 4;

        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            int workerId = i;
            tasks[workerId] = Task.Run(() =>
            {
                if (dedupDict.TryAdd(hash, workerId))
                {
                    // This worker won — write to packing channel
                    packChannel.Writer.TryWrite(hash);
                }
            });
        }

        await Task.WhenAll(tasks);
        packChannel.Writer.Complete();

        // Count items written
        int count = 0;
        await foreach (var _ in packChannel.Reader.ReadAllAsync())
            count++;

        // Specification: exactly 1 blob should have been written
        Microsoft.Coyote.Specifications.Specification.Assert(
            count == 1,
            $"Expected exactly 1 blob in channel, but found {count}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.3  Bounded channel producer-consumer — no deadlock in 500 interleavings
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BoundedChannel_ProducerConsumer_NoDeadlock()
    {
        var config = Configuration.Create()
            .WithTestingIterations(500)
            .WithMaxSchedulingSteps(1000);

        var engine = TestingEngine.Create(config, BoundedChannel_ConcurrentBody);
        engine.Run();

        var report = engine.TestReport;
        Assert.True(report.NumOfFoundBugs == 0,
            $"Coyote found {report.NumOfFoundBugs} deadlock(s) in bounded channel:\n{report.BugReports.FirstOrDefault()}");
    }

    private static async Task BoundedChannel_ConcurrentBody()
    {
        const int capacity    = 2;
        const int itemCount   = 8;

        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            FullMode      = BoundedChannelFullMode.Wait,
            SingleWriter  = true,
            SingleReader  = true,
        });

        // Producer
        var producer = Task.Run(async () =>
        {
            for (int i = 0; i < itemCount; i++)
                await channel.Writer.WriteAsync(i);
            channel.Writer.Complete();
        });

        // Consumer
        var consumed = 0;
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in channel.Reader.ReadAllAsync())
                consumed++;
        });

        await Task.WhenAll(producer, consumer);

        Microsoft.Coyote.Specifications.Specification.Assert(
            consumed == itemCount,
            $"Expected {itemCount} items consumed, got {consumed}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.4  Pipeline completion — all events delivered, writer properly closed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PipelineCompletion_AllEventsDeliveredAndWriterClosed()
    {
        var config = Configuration.Create()
            .WithTestingIterations(500)
            .WithMaxSchedulingSteps(2000);

        var engine = TestingEngine.Create(config, PipelineCompletion_ConcurrentBody);
        engine.Run();

        var report = engine.TestReport;
        Assert.True(report.NumOfFoundBugs == 0,
            $"Coyote found {report.NumOfFoundBugs} bug(s) in pipeline completion:\n{report.BugReports.FirstOrDefault()}");
    }

    private static async Task PipelineCompletion_ConcurrentBody()
    {
        const int eventCount = 5;
        var events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        // Multiple workers write events concurrently
        var writerTasks = new Task[eventCount];
        for (int i = 0; i < eventCount; i++)
        {
            int idx = i;
            writerTasks[idx] = Task.Run(() =>
            {
                events.Writer.TryWrite($"event-{idx}");
            });
        }

        await Task.WhenAll(writerTasks);

        // Complete the writer after all workers finish
        events.Writer.Complete();

        // Reader drains all events
        var received = new List<string>();
        await foreach (var ev in events.Reader.ReadAllAsync())
            received.Add(ev);

        // Specification: all events must be delivered, writer must be completed
        Microsoft.Coyote.Specifications.Specification.Assert(
            received.Count == eventCount,
            $"Expected {eventCount} events, received {received.Count}");

        Microsoft.Coyote.Specifications.Specification.Assert(
            events.Reader.Completion.IsCompleted,
            "Channel reader should be completed after writer is closed and all items consumed");
    }
}
