using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Handles shard serialization with compression and optional encryption.
/// Task 4.3.
/// </summary>
internal static class ShardSerializer
{
    /// <summary>
    /// Serializes a <see cref="Shard"/> to a compressed (and optionally encrypted) byte array.
    /// </summary>
    public static async Task<byte[]> SerializeAsync(
        Shard               shard,
        IEncryptionService  encryption,
        ICompressionService compression,
        CancellationToken   cancellationToken = default)
    {
        var ms = new MemoryStream();

        await using (var encStream         = encryption.WrapForEncryption(ms))
        await using (var compressionStream = compression.WrapForCompression(encStream))
        await using (var writer            = new StreamWriter(compressionStream, leaveOpen: true))
        {
            shard.WriteTo(writer);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a <see cref="Shard"/> from a compressed (and optionally encrypted) byte array.
    /// </summary>
    public static Shard Deserialize(
        byte[]              data,
        IEncryptionService  encryption,
        ICompressionService compression)
    {
        var ms         = new MemoryStream(data);
        var decStream  = encryption.WrapForDecryption(ms);
        var decompress = compression.WrapForDecompression(decStream);
        using var reader = new StreamReader(decompress);
        return Shard.ReadFrom(reader);
    }

    /// <summary>
    /// Deserializes a <see cref="Shard"/> from a readable stream (compression + optional encryption).
    /// </summary>
    public static Shard Deserialize(
        Stream              source,
        IEncryptionService  encryption,
        ICompressionService compression)
    {
        var decStream  = encryption.WrapForDecryption(source);
        var decompress = compression.WrapForDecompression(decStream);
        using var reader = new StreamReader(decompress);
        return Shard.ReadFrom(reader);
    }
}
