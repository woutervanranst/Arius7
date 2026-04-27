using System.IO.Compression;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Snapshot;

/// <summary>
/// Snapshot manifest: the root of a complete archive state.
/// Stored (gzip + optional encrypt) at <c>snapshots/&lt;timestamp&gt;</c>.
/// </summary>
public sealed record SnapshotManifest
{
    /// <summary>UTC timestamp of snapshot creation (ISO-8601 round-trip format).</summary>
    public required DateTimeOffset Timestamp   { get; init; }

    /// <summary>Root tree hash (SHA-256 hex, 64 chars) produced by the tree builder.</summary>
    public required FileTreeHash   RootHash    { get; init; }

    /// <summary>Total number of files in this snapshot.</summary>
    public required long           FileCount   { get; init; }

    /// <summary>Sum of original (uncompressed) sizes of all files in bytes.</summary>
    public required long           TotalSize   { get; init; }

    /// <summary>Arius tool version that created this snapshot.</summary>
    public required string         AriusVersion { get; init; }
}

/// <summary>
/// Serialization/deserialization for <see cref="SnapshotManifest"/>.
/// On-disk (Azure) format: JSON → gzip → optional encrypt.
/// </summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        Converters             = { new FileTreeHashJsonConverter() },
    };

    private sealed class FileTreeHashJsonConverter : JsonConverter<FileTreeHash>
    {
        public override FileTreeHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => FileTreeHash.Parse(reader.GetString() ?? throw new JsonException("Expected file tree hash string."));

        public override void Write(Utf8JsonWriter writer, FileTreeHash value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    // ── Serialize ─────────────────────────────────────────────────────────────

    public static async Task<byte[]> SerializeAsync(
        SnapshotManifest  manifest,
        IEncryptionService encryption,
        CancellationToken  cancellationToken = default)
    {
        var json  = JsonSerializer.SerializeToUtf8Bytes(manifest, s_options);
        var ms    = new MemoryStream();

        // gzip first, then optional encrypt
        await using (var encStream = encryption.WrapForEncryption(ms))
        await using (var gzip     = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(json, cancellationToken);
        }

        return ms.ToArray();
    }

    // ── Deserialize ───────────────────────────────────────────────────────────

    public static async Task<SnapshotManifest> DeserializeAsync(
        byte[]             bytes,
        IEncryptionService encryption,
        CancellationToken  cancellationToken = default)
    {
        var ms = new MemoryStream(bytes);
        await using var decStream = encryption.WrapForDecryption(ms);
        await using var gzip      = new GZipStream(decStream, CompressionMode.Decompress);
        var plain = new MemoryStream();
        await gzip.CopyToAsync(plain, cancellationToken);
        plain.Position = 0;

        return JsonSerializer.Deserialize<SnapshotManifest>(plain.ToArray(), s_options)
            ?? throw new InvalidDataException("Failed to deserialize snapshot manifest.");
    }
}

/// <summary>
/// Creates, lists, and resolves snapshots in blob storage, with a local plain-JSON disk cache.
///
/// Disk cache directory: <c>~/.arius/{accountName}-{containerName}/snapshots/</c>
///
/// Cache strategy:
/// <list type="bullet">
///   <item><see cref="CreateAsync"/>: write plain JSON to disk first, then upload (gzip + optional encrypt) to Azure.</item>
///   <item><see cref="ResolveAsync"/>: check disk first; fall back to Azure and cache locally.</item>
/// </list>
/// </summary>
public sealed class SnapshotService
{
    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly string                _diskCacheDir;

    /// <summary>
    /// Timestamp format used for snapshot blob names and local cache filenames.
    /// Lexicographic sort == chronological sort.
    /// e.g. <c>2026-03-22T150000.000Z</c>
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-ddTHHmmss.fffZ";

    private static readonly JsonSerializerOptions s_localJsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        Converters             = { new FileTreeHashJsonConverter() },
    };

    public SnapshotService(
        IBlobContainerService blobs,
        IEncryptionService    encryption,
        string                accountName,
        string                containerName)
    {
        _blobs        = blobs;
        _encryption   = encryption;
        _diskCacheDir = GetDiskCacheDirectory(accountName, containerName);
        Directory.CreateDirectory(_diskCacheDir);
    }

    // ── Directory helper ──────────────────────────────────────────────────────

    /// <summary>Returns <c>~/.arius/{accountName}-{containerName}/snapshots</c>.</summary>
    public static string GetDiskCacheDirectory(string accountName, string containerName)
        => RepositoryPaths.GetSnapshotCacheDirectory(accountName, containerName);

    // ── Snapshot blob name ────────────────────────────────────────────────────

    /// <summary>Returns the blob name for a snapshot with the given UTC timestamp.</summary>
    public static string BlobName(DateTimeOffset timestamp) =>
        BlobPaths.Snapshot(timestamp.UtcDateTime.ToString(TimestampFormat));

    /// <summary>Parses a snapshot timestamp from a blob name.</summary>
    public static DateTimeOffset ParseTimestamp(string blobName)
    {
        var name = blobName.StartsWith(BlobPaths.Snapshots, StringComparison.Ordinal)
            ? blobName[BlobPaths.Snapshots.Length..]
            : blobName;

        return DateTimeOffset.ParseExact(name, TimestampFormat, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new snapshot: writes plain JSON to disk first (write-through),
    /// then uploads (gzip + optional encrypt) to Azure.
    /// Returns the created manifest.
    /// </summary>
    public async Task<SnapshotManifest> CreateAsync(
        FileTreeHash      rootHash,
        long              fileCount,
        long              totalSize,
        DateTimeOffset?   timestamp         = null,
        CancellationToken cancellationToken = default)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = fileCount,
            TotalSize    = totalSize,
            AriusVersion = GetAriusVersion()
        };

        // Write-through: disk first (plain JSON)
        await WriteToDiskAsync(manifest, cancellationToken);

        // Then Azure (gzip + optional encrypt)
        var bytes    = await SnapshotSerializer.SerializeAsync(manifest, _encryption, cancellationToken);
        var blobName = BlobName(ts);

        await _blobs.UploadAsync(
            blobName,
            new MemoryStream(bytes),
            new Dictionary<string, string>(),
            BlobTier.Cool,
            _encryption.IsEncrypted ? ContentTypes.SnapshotGcmEncrypted : ContentTypes.SnapshotPlaintext,
            overwrite: false,
            cancellationToken: cancellationToken);

        return manifest;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all snapshot blob names sorted by timestamp (oldest → newest).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBlobNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        await foreach (var name in _blobs.ListAsync(BlobPaths.Snapshots, cancellationToken))
            names.Add(name);

        names.Sort((a, b) => ParseTimestamp(a).CompareTo(ParseTimestamp(b)));

        return names;
    }

    // ── Resolve ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a snapshot: disk-first (reads plain JSON if cached locally), falls back to Azure.
    /// Returns the latest if <paramref name="version"/> is <c>null</c>,
    /// otherwise returns the snapshot whose timestamp starts with the given version string.
    /// Returns <c>null</c> if no matching snapshot exists.
    /// </summary>
    public async Task<SnapshotManifest?> ResolveAsync(string? version = null, CancellationToken cancellationToken = default)
    {
        var names = await ListBlobNamesAsync(cancellationToken);
        if (names.Count == 0) return null;

        string blobName;
        if (version is null)
        {
            blobName = names[^1]; // latest (last after sort)
        }
        else
        {
            var match = names.LastOrDefault(n =>
            {
                var ts = n.StartsWith(BlobPaths.Snapshots) ? n[BlobPaths.Snapshots.Length..] : n;
                return ts.StartsWith(version, StringComparison.OrdinalIgnoreCase);
            });
            if (match is null) return null;
            blobName = match;
        }

        // Disk-first: check local plain-JSON cache
        var localName = blobName.StartsWith(BlobPaths.Snapshots) ? blobName[BlobPaths.Snapshots.Length..] : blobName;
        var localPath = Path.Combine(_diskCacheDir, localName);
        if (File.Exists(localPath))
        {
            var json = await File.ReadAllBytesAsync(localPath, cancellationToken);
            return JsonSerializer.Deserialize<SnapshotManifest>(json, s_localJsonOptions)
                ?? throw new InvalidDataException($"Failed to deserialize local snapshot: {localPath}");
        }

        // Fall back to Azure and cache locally
        var manifest = await LoadFromAzureAsync(blobName, cancellationToken);
        await WriteToDiskAsync(manifest, cancellationToken);
        return manifest;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task WriteToDiskAsync(SnapshotManifest manifest, CancellationToken cancellationToken)
    {
        var fileName = manifest.Timestamp.UtcDateTime.ToString(TimestampFormat);
        var path     = Path.Combine(_diskCacheDir, fileName);
        var json     = JsonSerializer.SerializeToUtf8Bytes(manifest, s_localJsonOptions);
        await File.WriteAllBytesAsync(path, json, cancellationToken);
    }

    private async Task<SnapshotManifest> LoadFromAzureAsync(
        string blobName, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SnapshotSerializer.DeserializeAsync(ms.ToArray(), _encryption, cancellationToken);
    }

    private static string GetAriusVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
}
