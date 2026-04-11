namespace Arius.Core.Tests.Shared.Encryption;

/// <summary>Stream that discards all writes and counts bytes.</summary>
internal sealed class DevNullStream : Stream
{
    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
    public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
