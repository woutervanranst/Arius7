using Arius.Core.Shared.Streaming;
using Shouldly;

namespace Arius.Core.Tests.Shared.Streaming;

public class CountingStreamTests
{
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

    [Test]
    public void BytesWritten_AvailableAfterDispose()
    {
        var cs = new CountingStream(new MemoryStream());
        cs.Write(new byte[100]);
        cs.Write(new byte[200]);
        cs.Dispose();

        cs.BytesWritten.ShouldBe(300);
    }

    [Test]
    public async Task WriteAsync_TracksBytesWritten()
    {
        using var dst = new MemoryStream();
        using var cs  = new CountingStream(dst);

        await cs.WriteAsync(new byte[1024]);
        await cs.WriteAsync(new byte[512]);

        cs.BytesWritten.ShouldBe(1536);
    }

    [Test]
    public async Task WriteAsync_WithOffsetCount_TracksBytesWritten()
    {
        using var dst = new MemoryStream();
        using var cs  = new CountingStream(dst);
        var buffer = new byte[1024];

        await cs.WriteAsync(buffer, 100, 512, CancellationToken.None);

        cs.BytesWritten.ShouldBe(512);
        dst.Length.ShouldBe(512);
    }

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
