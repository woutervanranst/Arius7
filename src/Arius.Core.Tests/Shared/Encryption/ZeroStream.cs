namespace Arius.Core.Tests.Shared.Encryption;

internal sealed class ZeroStream(long length) : Stream
{
    private long _remaining = length;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => length;
    public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, _remaining);
        Array.Clear(buffer, offset, n);
        _remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
