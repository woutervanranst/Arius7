namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Read-only pass-through stream that feeds every byte it serves into a <see cref="SparseFingerprint.Sampler"/>,
/// so the sparse fingerprint is produced for free while the file is fully hashed for upload.
/// Same read-wrapping shape as <see cref="Arius.Core.Shared.Streaming.ProgressStream"/>, but captures
/// fingerprint samples instead of reporting progress.
/// </summary>
internal sealed class SparseSamplingStream : Stream
{
    private readonly Stream                    _inner;
    private readonly SparseFingerprint.Sampler _sampler;
    private long                               _position;

    public SparseSamplingStream(Stream inner, long size)
    {
        _inner   = inner;
        _sampler = new SparseFingerprint.Sampler(size);
    }

    public byte[] Fingerprint() => _sampler.Finish();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _sampler.Capture(_position, buffer.AsSpan(offset, n));
            _position += n;
        }

        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        if (n > 0)
        {
            _sampler.Capture(_position, buffer.Span[..n]);
            _position += n;
        }

        return n;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _inner.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
    public override void SetLength(long value)                       => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) 
            _inner.Dispose(); 
        
        base.Dispose(disposing); 
    }
}
