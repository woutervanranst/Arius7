using System.Diagnostics;

namespace Arius.Core.Shared.Streaming;

/// <summary>
/// Read-mode stream wrapper that reports cumulative bytes read via <see cref="IProgress{T}"/>.
/// Delegates all reads to the inner stream and does not buffer any data.
///
/// The first read reports immediately; after that reports are coalesced to at most one per
/// <see cref="ReportInterval"/>. Consumers wrap the callback in
/// <see cref="Progress{T}"/>, which posts a thread-pool work item per report, so reporting after every
/// read queued one work item per buffer — roughly one per 64-80 KiB of every archived and restored byte.
/// The true total is always emitted once the source reaches EOF (a read returning 0), and again on
/// dispose for a stream abandoned before EOF, so a consumer never ends up short of the real figure.
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
    /// Reports the running total, but at most once per <see cref="ReportInterval"/>.
    /// Uses <see cref="Stopwatch"/> rather than wall-clock time so it is monotonic.
    /// </summary>
    private void ReportThrottled()
    {
        var now = Stopwatch.GetTimestamp();

        // The first read always reports, so a consumer sees work start immediately rather than after a
        // blank interval — and so a stream consumed in a single read still reports before EOF.
        if (_hasReported && Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < ReportInterval)
            return;

        _hasReported         = true;
        _lastReportTimestamp = now;
        _reportedBytes       = _bytesRead;
        _progress.Report(_bytesRead);
    }

    /// <summary>
    /// Emits the running total if the throttle has held anything back. Called at EOF and on dispose, so a
    /// consumer is never left short of the real figure by up to one interval's worth of bytes.
    /// </summary>
    private void ReportFinal()
    {
        // A source that yielded nothing reports nothing — an empty stream must not emit a spurious 0.
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
