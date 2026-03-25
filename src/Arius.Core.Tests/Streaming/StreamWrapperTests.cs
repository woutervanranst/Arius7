using Arius.Core.Streaming;
using Shouldly;

namespace Arius.Core.Tests.Streaming;

/// <summary>Synchronous IProgress implementation for deterministic testing.</summary>
file sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

public class ProgressStreamTests
{
    // ── Progress reported on read ──────────────────────────────────────────────

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

        // 4 chunks of 256 → 256, 512, 768, 1024
        reports.Count.ShouldBe(4);
        reports[^1].ShouldBe(1024);
        reports.ShouldBeInOrder();
    }

    // ── Final progress equals source length ───────────────────────────────────

    [Test]
    public async Task ReadAsync_FinalProgressEqualsLength()
    {
        var data   = new byte[3000];
        Random.Shared.NextBytes(data);
        using var src = new MemoryStream(data);
        long lastReport = 0;
        var progress    = new SyncProgress<long>(v => lastReport = v);
        using var ps    = new ProgressStream(src, progress);

        // CopyToAsync uses ReadAsync internally
        using var dst = new MemoryStream();
        await ps.CopyToAsync(dst);

        lastReport.ShouldBe(3000);
    }

    // ── Zero-length file ──────────────────────────────────────────────────────

    [Test]
    public void Read_ZeroLengthSource_NoProgressReported()
    {
        using var src  = new MemoryStream([]);
        int reportCount = 0;
        var progress    = new SyncProgress<long>(_ => reportCount++);
        using var ps    = new ProgressStream(src, progress);

        var buf = new byte[256];
        var n   = ps.Read(buf, 0, buf.Length);

        n.ShouldBe(0);
        reportCount.ShouldBe(0);
    }

    // ── Span overload ─────────────────────────────────────────────────────────

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

public class CountingStreamTests
{
    // ── BytesWritten tracks all writes ────────────────────────────────────────

    [Test]
    public void Write_TracksBytesWritten()
    {
        using var dst      = new MemoryStream();
        using var cs       = new CountingStream(dst);
        var chunk1         = new byte[512];
        var chunk2         = new byte[256];

        cs.Write(chunk1, 0, chunk1.Length);
        cs.Write(chunk2, 0, chunk2.Length);

        cs.BytesWritten.ShouldBe(768);
        dst.Length.ShouldBe(768);
    }

    // ── BytesWritten available after dispose ──────────────────────────────────

    [Test]
    public void BytesWritten_AvailableAfterDispose()
    {
        var cs = new CountingStream(new MemoryStream());
        cs.Write(new byte[100]);
        cs.Write(new byte[200]);
        cs.Dispose();

        // BytesWritten must still be readable after Dispose
        cs.BytesWritten.ShouldBe(300);
    }

    // ── Async write ───────────────────────────────────────────────────────────

    [Test]
    public async Task WriteAsync_TracksBytesWritten()
    {
        using var dst = new MemoryStream();
        using var cs  = new CountingStream(dst);

        await cs.WriteAsync(new byte[1024]);
        await cs.WriteAsync(new byte[512]);

        cs.BytesWritten.ShouldBe(1536);
    }

    // ── Span write ────────────────────────────────────────────────────────────

    [Test]
    public void WriteSpan_TracksBytesWritten()
    {
        using var dst = new MemoryStream();
        using var cs  = new CountingStream(dst);

        ReadOnlySpan<byte> buf = new byte[64];
        cs.Write(buf);

        cs.BytesWritten.ShouldBe(64);
    }
}
