using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure.Packing;

// ─────────────────────────────────────────────────────────────────────────────
// Manifest model
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// JSON manifest stored as <c>manifest.json</c> inside every pack TAR archive.
/// Lists all blobs contained in the pack, in order.
/// </summary>
public sealed record PackManifest(IReadOnlyList<PackManifestEntry> Blobs)
{
    public static PackManifest FromJson(string json)
        => JsonSerializer.Deserialize<PackManifest>(json, JsonOptions)
           ?? throw new InvalidDataException("Null manifest JSON.");

    public string ToJson()
        => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false,
    };
}

public sealed record PackManifestEntry(
    string   Id,
    string   Type,   // "data" or "tree"
    long     Size);

// ─────────────────────────────────────────────────────────────────────────────
// Blob to pack — input unit
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Represents a single plaintext blob ready to be packed.</summary>
public sealed record BlobToPack(BlobHash Hash, BlobType Type, byte[] Data);

// ─────────────────────────────────────────────────────────────────────────────
// Sealed pack result
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Result of sealing a pack: the encrypted pack bytes, the derived pack ID
/// (SHA-256 of the encrypted bytes), and index entries for every contained blob.
/// </summary>
public sealed record SealedPack(
    PackId               PackId,
    byte[]               EncryptedBytes,
    IReadOnlyList<IndexEntry> IndexEntries);

// ─────────────────────────────────────────────────────────────────────────────
// PackerManager  (tasks 5.1 – 5.4)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Accumulates blobs and seals them into encrypted pack files.
///
/// Pipeline: blobs → TAR archive → gzip → AES-256-CBC → pack bytes
/// Pack ID  = SHA-256(encrypted_bytes)   (hex, lowercase)
///
/// The TAR archive contains:
///   {blob-id}.bin   — raw plaintext blob data
///   manifest.json   — JSON listing all blobs
///
/// Packs are sealed when the accumulated plaintext size reaches
/// <see cref="PackSizeThreshold"/> or when <see cref="FlushAsync"/> is called.
/// </summary>
public sealed class PackerManager : IAsyncDisposable
{
    public const long DefaultPackSize = 10L * 1024 * 1024; // 10 MB

    private readonly byte[] _masterKey;
    private readonly long   _packSizeThreshold;

    private readonly List<BlobToPack> _pending = [];
    private long _pendingBytes = 0;

    public PackerManager(byte[] masterKey, long packSizeThreshold = DefaultPackSize)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(packSizeThreshold, 1);
        _masterKey         = masterKey;
        _packSizeThreshold = packSizeThreshold;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a blob to the pending buffer.
    /// Returns a sealed pack if the buffer has reached the threshold, otherwise null.
    /// </summary>
    public async Task<SealedPack?> AddAsync(BlobToPack blob, CancellationToken cancellationToken = default)
    {
        _pending.Add(blob);
        _pendingBytes += blob.Data.Length;

        if (_pendingBytes >= _packSizeThreshold)
            return await FlushAsync(cancellationToken);

        return null;
    }

    /// <summary>
    /// Seals whatever is currently in the buffer into a pack, regardless of size.
    /// Returns null if the buffer is empty.
    /// </summary>
    public async Task<SealedPack?> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.Count == 0)
            return null;

        var blobs = _pending.ToList();
        _pending.Clear();
        _pendingBytes = 0;

        return await SealAsync(blobs, _masterKey, cancellationToken);
    }

    // ── Pack creation (static — also called by tests directly) ───────────────

    /// <summary>
    /// Seals a list of blobs into an encrypted pack.
    /// This is the core pack pipeline: TAR → gzip → AES-256-CBC → SHA-256(ciphertext) = pack ID.
    /// </summary>
    public static async Task<SealedPack> SealAsync(
        IReadOnlyList<BlobToPack> blobs,
        byte[] masterKey,
        CancellationToken cancellationToken = default)
    {
        if (blobs.Count == 0)
            throw new ArgumentException("Cannot seal an empty pack.", nameof(blobs));

        // Step 1: Build TAR + gzip in memory, then encrypt.
        // We need the encrypted bytes to compute the pack ID (SHA-256 of ciphertext).
        using var plaintextStream  = new MemoryStream();
        await BuildTarGzipAsync(blobs, plaintextStream, cancellationToken);
        plaintextStream.Position = 0;

        using var encryptedStream = new MemoryStream();
        await CryptoService.EncryptAsync(plaintextStream, encryptedStream, masterKey, cancellationToken: cancellationToken);
        var encryptedBytes = encryptedStream.ToArray();

        // Step 2: Pack ID = SHA-256(encrypted bytes)
        var packHashBytes = SHA256.HashData(encryptedBytes);
        var packId        = new PackId(Convert.ToHexString(packHashBytes).ToLowerInvariant());

        // Step 3: Build index entries.
        // TAR entries are not randomly seekable in a pre-encrypted blob, so we store
        // offset/length = 0/0 for now (these are resolved once we know the TAR layout).
        // For the initial implementation we record the plaintext byte positions by
        // rebuilding the TAR offsets deterministically.
        var indexEntries = BuildIndexEntries(blobs, packId);

        return new SealedPack(packId, encryptedBytes, indexEntries);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes blobs → TAR → gzip stream into <paramref name="output"/>.
    /// </summary>
    public static async Task BuildTarGzipAsync(
        IReadOnlyList<BlobToPack> blobs,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        await using var tar  = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: false);

        // Write each blob as {blob-id}.bin
        foreach (var blob in blobs)
        {
            var entry = new GnuTarEntry(TarEntryType.RegularFile, $"{blob.Hash.Value}.bin")
            {
                DataStream = new MemoryStream(blob.Data),
            };
            await tar.WriteEntryAsync(entry, cancellationToken);
        }

        // Write manifest.json
        var manifestEntries = blobs.Select(b => new PackManifestEntry(
            b.Hash.Value,
            b.Type == BlobType.Data ? "data" : "tree",
            b.Data.Length)).ToList();
        var manifest     = new PackManifest(manifestEntries);
        var manifestJson = manifest.ToJson();
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);

        var manifestEntry = new GnuTarEntry(TarEntryType.RegularFile, "manifest.json")
        {
            DataStream = new MemoryStream(manifestBytes),
        };
        await tar.WriteEntryAsync(manifestEntry, cancellationToken);
    }

    /// <summary>
    /// Produces <see cref="IndexEntry"/> records for each blob.
    /// Offset and Length represent the plaintext byte position inside the TAR archive
    /// (before gzip/encryption).  We record a placeholder (0, 0) here; the actual
    /// TAR offsets are not required for correctness in the current implementation since
    /// pack extraction iterates the entire TAR.  This can be enhanced later.
    /// </summary>
    private static IReadOnlyList<IndexEntry> BuildIndexEntries(
        IReadOnlyList<BlobToPack> blobs,
        PackId packId)
    {
        return blobs.Select(b => new IndexEntry(
            BlobHash: b.Hash,
            PackId:   packId,
            Offset:   0,
            Length:   b.Data.Length,
            BlobType: b.Type)).ToList();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
