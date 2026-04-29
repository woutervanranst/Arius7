namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

internal sealed class ChunkedReadMemoryStream(byte[] buffer, int maxChunkSize) : MemoryStream(buffer, writable: false)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        base.Read(buffer, offset, Math.Min(count, maxChunkSize));

    public override int Read(Span<byte> buffer) =>
        base.Read(buffer[..Math.Min(buffer.Length, maxChunkSize)]);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        base.ReadAsync(buffer, offset, Math.Min(count, maxChunkSize), cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, maxChunkSize)], cancellationToken);
}
