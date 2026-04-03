namespace Arius.Core.Shared.Streaming;

/// <summary>
/// Read-mode stream wrapper that reports cumulative bytes read via <see cref="IProgress{T}"/>.
/// Delegates all reads to the inner stream and reports progress after each read operation.
/// Does not buffer any data.
/// </summary>
public sealed class ProgressStream : Stream
{
    private readonly Stream          _inner;
    private readonly IProgress<long> _progress;
    private long                     _bytesRead;

    /// <param name="inner">The readable source stream.</param>
    /// <param name="progress">Receives cumulative bytes read after each read call.</param>
    public ProgressStream(Stream inner, IProgress<long> progress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(progress);
        if (!inner.CanRead)
            throw new ArgumentException("Inner stream must be readable.", nameof(inner));

        _inner    = inner;
        _progress = progress;
    }

    public override bool CanRead  => true;
    public override bool CanWrite => false;
    public override bool CanSeek  => false;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _bytesRead += n;
            _progress.Report(_bytesRead);
        }
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0)
        {
            _bytesRead += n;
            _progress.Report(_bytesRead);
        }
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer, offset, count, ct);
        if (n > 0)
        {
            _bytesRead += n;
            _progress.Report(_bytesRead);
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        if (n > 0)
        {
            _bytesRead += n;
            _progress.Report(_bytesRead);
        }
        return n;
    }

    public override void Flush() => _inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override long Length   => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
    public override void SetLength(long value)                       => throw new NotSupportedException();
}
