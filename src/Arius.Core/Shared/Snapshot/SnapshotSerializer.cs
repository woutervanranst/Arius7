using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;

namespace Arius.Core.Shared.Snapshot;

/// <summary>
/// Serialization/deserialization for the snapshot blob payload stored in remote blob storage.
/// This serializer is for the Azure wire format only: JSON → compress → optional encrypt.
/// </summary>
internal static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        Converters             = { new FileTreeHashJsonConverter() },
    };

    // ── Serialize ─────────────────────────────────────────────────────────────

    public static async Task<byte[]> SerializeAsync(
        SnapshotManifest   manifest,
        IEncryptionService encryption,
        ICompressionService compression,
        CancellationToken  cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, s_options);
        var ms   = new MemoryStream();

        // compress first, then optional encrypt
        await using (var encStream         = encryption.WrapForEncryption(ms))
        await using (var compressionStream = compression.WrapForCompression(encStream))
        {
            await compressionStream.WriteAsync(json, cancellationToken);
        }

        return ms.ToArray();
    }

    // ── Deserialize ───────────────────────────────────────────────────────────

    public static async Task<SnapshotManifest> DeserializeAsync(
        byte[]              bytes,
        IEncryptionService  encryption,
        ICompressionService compression,
        CancellationToken   cancellationToken = default)
    {
        var             ms         = new MemoryStream(bytes);
        await using var decStream  = encryption.WrapForDecryption(ms);
        await using var decompress = compression.WrapForDecompression(decStream);
        var             plain      = new MemoryStream();
        await decompress.CopyToAsync(plain, cancellationToken);
        plain.Position = 0;

        return JsonSerializer.Deserialize<SnapshotManifest>(plain.ToArray(), s_options)
               ?? throw new InvalidDataException("Failed to deserialize snapshot manifest.");
    }
}