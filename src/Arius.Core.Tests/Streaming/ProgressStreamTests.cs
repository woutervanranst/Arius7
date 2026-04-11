using Arius.Core.Shared.Streaming;
using Shouldly;

namespace Arius.Core.Tests.Streaming;

/// <summary>Synchronous IProgress implementation for deterministic testing.</summary>
file sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

public class ProgressStreamTests
{
    [Test]
    public void Read_ReportsProgressAfterEachChunk()
    {
        var data     = new byte[1024];
        Random.Shared.NextBytes(data);
        using var src  = new MemoryStream(data);
        var reports    = new List<long>();
        var progress   = new SyncProgress<long>(v => reports.Add(v));
        using var ps   = new ProgressStream(src, progress);

        var buf = new byte[256];
        while (ps.Read(buf, 0, buf.Length) > 0) { }

        reports.Count.ShouldBe(4);
        reports[^1].ShouldBe(1024);
        reports.ShouldBeInOrder();
    }

    [Test]
    public async Task ReadAsync_FinalProgressEqualsLength()
    {
        var data   = new byte[3000];
        Random.Shared.NextBytes(data);
        using var src = new MemoryStream(data);
        long lastReport = 0;
        var progress    = new SyncProgress<long>(v => lastReport = v);
        using var ps    = new ProgressStream(src, progress);

        using var dst = new MemoryStream();
        await ps.CopyToAsync(dst);

        lastReport.ShouldBe(3000);
    }

    [Test]
    public async Task ReadAsync_ByteArrayOverload_ReportsNonDecreasingProgress()
    {
        var data   = new byte[3000];
        Random.Shared.NextBytes(data);
        using var src = new MemoryStream(data);
        var reports = new List<long>();
        var progress = new SyncProgress<long>(value => reports.Add(value));
        using var ps = new ProgressStream(src, progress);

        var buffer = new byte[512];
        while (await ps.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) > 0)
        {
        }

        reports.ShouldNotBeEmpty();
        reports.ShouldBeInOrder();
        reports[^1].ShouldBe(data.Length);
    }

    [Test]
    public async Task ReadAsync_MemoryOverload_ReportsNonDecreasingProgress()
    {
        var data   = new byte[3000];
        Random.Shared.NextBytes(data);
        using var src = new MemoryStream(data);
        var reports = new List<long>();
        var progress = new SyncProgress<long>(value => reports.Add(value));
        using var ps = new ProgressStream(src, progress);

        var buffer = new byte[700];
        while (await ps.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
        {
        }

        reports.ShouldNotBeEmpty();
        reports.ShouldBeInOrder();
        reports[^1].ShouldBe(data.Length);
    }

    [Test]
    public void Read_ZeroLengthSource_NoProgressReported()
    {
        using var src  = new MemoryStream([]);
        var reportCount = 0;
        var progress    = new SyncProgress<long>(_ => reportCount++);
        using var ps    = new ProgressStream(src, progress);

        var buf = new byte[256];
        var n   = ps.Read(buf, 0, buf.Length);

        n.ShouldBe(0);
        reportCount.ShouldBe(0);
    }

    [Test]
    public void ReadSpan_ReportsProgress()
    {
        var data  = new byte[100];
        Random.Shared.NextBytes(data);
        using var src  = new MemoryStream(data);
        long lastReport = 0;
        var progress    = new SyncProgress<long>(v => lastReport = v);
        using var ps    = new ProgressStream(src, progress);

        Span<byte> buf = stackalloc byte[100];
        var n = ps.Read(buf);

        n.ShouldBe(100);
        lastReport.ShouldBe(100);
    }
}
