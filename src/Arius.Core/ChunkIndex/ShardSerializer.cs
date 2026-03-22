using System.IO.Compression;
using Arius.Core.Encryption;
using Arius.Core.Storage;

namespace Arius.Core.ChunkIndex;

/// <summary>
/// Handles shard serialization with gzip compression and optional encryption.
/// Task 4.3.
/// </summary>
public static class ShardSerializer
{
    /// <summary>
    /// Serializes a <see cref="Shard"/> to a gzip-compressed (and optionally encrypted) byte array.
    /// </summary>
    public static async Task<byte[]> SerializeAsync(
        Shard             shard,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();

        await using (var encStream  = encryption.WrapForEncryption(ms))
        await using (var gzipStream = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer     = new StreamWriter(gzipStream, leaveOpen: true))
        {
            shard.WriteTo(writer);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a <see cref="Shard"/> from a gzip-compressed (and optionally encrypted) byte array.
    /// </summary>
    public static Shard Deserialize(
        byte[]            data,
        IEncryptionService encryption)
    {
        var ms         = new MemoryStream(data);
        var decStream  = encryption.WrapForDecryption(ms);
        var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        return Shard.ReadFrom(reader);
    }

    /// <summary>
    /// Deserializes a <see cref="Shard"/> from a readable stream (gzip + optional encryption).
    /// </summary>
    public static Shard DeserializeFromStream(
        Stream             source,
        IEncryptionService encryption)
    {
        var decStream  = encryption.WrapForDecryption(source);
        var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        return Shard.ReadFrom(reader);
    }
}
