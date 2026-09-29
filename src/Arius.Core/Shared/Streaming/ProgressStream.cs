namespace Arius.Core.Shared.Streaming;

/// <summary>
/// Read-mode stream wrapper that reports cumulative bytes read without buffering.
/// The first read reports immediately; later reports are limited to one per
/// <see cref="ReportInterval"/>. The final total is emitted at EOF or disposal.
/// </summary>
public sealed class ProgressStream : Stream
{
    /// <summary>Minimum wall-clock gap between two progress callbacks.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(500);

    private readonly Stream          _inner;
    private readonly IProgress<long> _progress;
    private readonly TimeProvider    _timeProvider;
    private long                     _bytesRead;
    private long                     _reportedBytes;
    private long                     _lastReportTimestamp;

    /// <param name="inner">The readable source stream.</param>
    /// <param name="progress">Receives the cumulative bytes read, throttled as described on the type.</param>
    public ProgressStream(Stream inner, IProgress<long> progress)
        : this(inner, progress, TimeProvider.System)
    {
    }

    internal ProgressStream(Stream inner, IProgress<long> progress, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(progress);
        if (!inner.CanRead)
            throw new ArgumentException("Inner stream must be readable.", nameof(inner));

        _inner        = inner;
        _progress     = progress;
        _timeProvider = timeProvider;
    }

    /// <summary>Accounts for one inner read of <paramref name="read"/> bytes out of <paramref name="requested"/>.</summary>
    private int OnRead(int read, int requested)
    {
        if (read > 0)
        {
            _bytesRead += read;
            ReportThrottled();
        }
        else if (requested != 0) // a zero-length read is not EOF
        {
            ReportFinal();
        }

        return read;
    }

    /// <summary>
    /// Reports the running total at most once per <see cref="ReportInterval"/> using monotonic time.
    /// </summary>
    private void ReportThrottled()
    {
        var now = _timeProvider.GetTimestamp();

        if (_reportedBytes > 0 && _timeProvider.GetElapsedTime(_lastReportTimestamp, now) < ReportInterval)
            return;

        _lastReportTimestamp = now;
        _reportedBytes       = _bytesRead;
        _progress.Report(_bytesRead);
    }

    /// <summary>
    /// Reports any total withheld by throttling at EOF or disposal.
    /// </summary>
    private void ReportFinal()
    {
        // Also keeps an empty source from emitting a spurious zero.
        if (_reportedBytes == _bytesRead)
            return;

        _reportedBytes = _bytesRead;
        _progress.Report(_bytesRead);
    }

    public override bool CanRead  => true;
    public override bool CanWrite => false;
    public override bool CanSeek  => false;

    public override int Read(byte[] buffer, int offset, int count) => OnRead(_inner.Read(buffer, offset, count), count);

    public override int Read(Span<byte> buffer) => OnRead(_inner.Read(buffer), buffer.Length);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => OnRead(await _inner.ReadAsync(buffer, offset, count, ct), count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => OnRead(await _inner.ReadAsync(buffer, ct), buffer.Length);

    public override void Flush() => _inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReportFinal();
            _inner.Dispose();
        }

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
