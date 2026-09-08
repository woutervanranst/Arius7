using System.Diagnostics;

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
    private long                     _bytesRead;
    private long                     _reportedBytes;
    private long                     _lastReportTimestamp;
    private bool                     _hasReported;

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

    /// <summary>
    /// Reports the running total at most once per <see cref="ReportInterval"/> using monotonic time.
    /// </summary>
    private void ReportThrottled()
    {
        var now = Stopwatch.GetTimestamp();

        if (_hasReported && Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < ReportInterval)
            return;

        _hasReported         = true;
        _lastReportTimestamp = now;
        _reportedBytes       = _bytesRead;
        _progress.Report(_bytesRead);
    }

    /// <summary>
    /// Reports any total withheld by throttling at EOF or disposal.
    /// </summary>
    private void ReportFinal()
    {
        // Empty sources do not emit a spurious zero.
        if (_bytesRead == 0)
            return;

        if (_hasReported && _reportedBytes == _bytesRead)
            return;

        _hasReported   = true;
        _reportedBytes = _bytesRead;
        _progress.Report(_bytesRead);
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
            ReportThrottled();
        }
        else
        {
            ReportFinal();
        }

        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0)
        {
            _bytesRead += n;
            ReportThrottled();
        }
        else
        {
            ReportFinal();
        }

        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer, offset, count, ct);
        if (n > 0)
        {
            _bytesRead += n;
            ReportThrottled();
        }
        else
        {
            ReportFinal();
        }

        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        if (n > 0)
        {
            _bytesRead += n;
            ReportThrottled();
        }
        else
        {
            ReportFinal();
        }

        return n;
    }

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
