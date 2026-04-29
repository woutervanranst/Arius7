namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

/// <summary>
/// Caps each read to a small chunk so upload tests can exercise retry and progress behavior with
/// streams that do not satisfy reads in a single call.
/// </summary>
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
