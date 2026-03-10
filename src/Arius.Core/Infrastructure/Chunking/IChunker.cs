using Arius.Core.Models;

namespace Arius.Core.Infrastructure.Chunking;

/// <summary>
/// Content-defined chunker interface.
/// Implementations split a stream into variable-size <see cref="Chunk"/> values
/// whose boundaries are determined by content, not position.
/// </summary>
public interface IChunker
{
    /// <summary>
    /// Splits <paramref name="stream"/> into a sequence of chunks.
    /// The concatenation of all chunk data exactly equals the original stream content.
    /// </summary>
    IAsyncEnumerable<Chunk> ChunkAsync(Stream stream, CancellationToken cancellationToken = default);
}
