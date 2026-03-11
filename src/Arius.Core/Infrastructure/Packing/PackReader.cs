using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure.Packing;

/// <summary>
/// Extracts blobs from encrypted pack files.
///
/// Extraction pipeline (inverse of <see cref="PackerManager.SealAsync"/>):
///   encrypted bytes → AES-256-CBC decrypt → gunzip → untar → read manifest + blobs
/// </summary>
public static class PackReader
{
    /// <summary>
    /// Decrypts and extracts all blobs from <paramref name="encryptedPackBytes"/>.
    /// Returns a dictionary keyed by blob hash (hex string) → blob plaintext bytes.
    /// The <paramref name="manifest"/> output contains the manifest parsed from the pack.
    /// </summary>
    public static async Task<(Dictionary<string, byte[]> Blobs, PackManifest Manifest)> ExtractAsync(
        byte[]   encryptedPackBytes,
        byte[]   masterKey,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Decrypt
        using var encryptedStream = new MemoryStream(encryptedPackBytes);
        using var plaintextStream = new MemoryStream();
        await CryptoService.DecryptAsync(encryptedStream, plaintextStream, masterKey, cancellationToken: cancellationToken);
        plaintextStream.Position = 0;

        // Step 2: Decompress (gunzip)
        using var gzip = new GZipStream(plaintextStream, CompressionMode.Decompress, leaveOpen: true);

        // Step 3: Untar — collect all entries
        var rawEntries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var tar  = new TarReader(gzip, leaveOpen: false);

        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.DataStream is null)
                continue;

            using var buf = new MemoryStream();
            await entry.DataStream.CopyToAsync(buf, cancellationToken);
            rawEntries[entry.Name] = buf.ToArray();
        }

        // Step 4: Parse manifest
        if (!rawEntries.TryGetValue("manifest.json", out var manifestBytes))
            throw new InvalidDataException("Pack does not contain manifest.json.");

        var manifestJson = Encoding.UTF8.GetString(manifestBytes);
        var manifest     = PackManifest.FromJson(manifestJson);

        // Step 5: Return blob map (keyed by blob ID without .bin extension)
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in manifest.Blobs)
        {
            var key = $"{entry.Id}.bin";
            if (rawEntries.TryGetValue(key, out var data))
                blobs[entry.Id] = data;
            else
                throw new InvalidDataException($"Manifest references blob '{entry.Id}' but it is not in the TAR.");
        }

        return (blobs, manifest);
    }

    /// <summary>
    /// Extracts a single blob by its <see cref="BlobHash"/> from an encrypted pack.
    /// Throws <see cref="KeyNotFoundException"/> if the blob is not present.
    /// </summary>
    public static async Task<byte[]> ExtractBlobAsync(
        byte[]   encryptedPackBytes,
        byte[]   masterKey,
        BlobHash blobHash,
        CancellationToken cancellationToken = default)
    {
        var (blobs, _) = await ExtractAsync(encryptedPackBytes, masterKey, cancellationToken);

        if (!blobs.TryGetValue(blobHash.Value, out var data))
            throw new KeyNotFoundException($"Blob '{blobHash.Value}' not found in pack.");

        return data;
    }
}
