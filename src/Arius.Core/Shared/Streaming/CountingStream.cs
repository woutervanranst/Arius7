namespace Arius.Core.Shared.Streaming;

/// <summary>
/// Write-mode stream wrapper that tracks the total bytes written via <see cref="BytesWritten"/>.
/// Delegates all writes to the inner stream without buffering.
/// <see cref="BytesWritten"/> remains readable after the stream is disposed.
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    /// <param name="inner">The writable destination stream.</param>
    public CountingStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite)
            throw new ArgumentException("Inner stream must be writable.", nameof(inner));

        _inner = inner;
    }

    /// <summary>Total bytes written to the inner stream so far. Readable after dispose.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _inner.WriteAsync(buffer, offset, count, ct);
        BytesWritten += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();
}
