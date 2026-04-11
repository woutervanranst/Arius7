namespace Arius.Core.Tests.Shared.ChunkStorage;

internal sealed class NonSeekableReadStream(byte[] content) : MemoryStream(content, writable: false)
{
    public override bool CanSeek => false;

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }
}
