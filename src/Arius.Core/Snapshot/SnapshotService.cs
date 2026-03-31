using Arius.Core.Encryption;
using Arius.Core.Storage;
using System.IO.Compression;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arius.Core.Snapshot;

/// <summary>
/// Snapshot manifest: the root of a complete archive state.
/// Stored (gzip + optional encrypt) at <c>snapshots/&lt;timestamp&gt;</c>.
/// </summary>
public sealed record SnapshotManifest
{
    /// <summary>UTC timestamp of snapshot creation (ISO-8601 round-trip format).</summary>
    public required DateTimeOffset Timestamp   { get; init; }

    /// <summary>Root tree hash (SHA-256 hex, 64 chars) produced by the tree builder.</summary>
    public required string         RootHash    { get; init; }

    /// <summary>Total number of files in this snapshot.</summary>
    public required long           FileCount   { get; init; }

    /// <summary>Sum of original (uncompressed) sizes of all files in bytes.</summary>
    public required long           TotalSize   { get; init; }

    /// <summary>Arius tool version that created this snapshot.</summary>
    public required string         AriusVersion { get; init; }
}

/// <summary>
/// Serialization/deserialization for <see cref="SnapshotManifest"/>.
/// On-disk format: JSON → gzip → optional encrypt.
/// </summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };

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
/// Creates, lists, and resolves snapshots in blob storage.
/// </summary>
public sealed class SnapshotService
{
    private readonly IBlobStorageService _blobs;
    private readonly IEncryptionService  _encryption;

    /// <summary>
    /// Timestamp format used for snapshot blob names.
    /// e.g. <c>2026-03-22T150000.000Z</c>
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-ddTHHmmss.fffZ";

    public SnapshotService(IBlobStorageService blobs, IEncryptionService encryption)
    {
        _blobs      = blobs;
        _encryption = encryption;
    }

    // ── Snapshot name ─────────────────────────────────────────────────────────

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

    // ── Task 6.2: Create ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new snapshot, serializes it, and uploads to <c>snapshots/&lt;timestamp&gt;</c>.
    /// Returns the uploaded snapshot.
    /// </summary>
    public async Task<SnapshotManifest> CreateAsync(
        string            rootHash,
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

    // ── Task 6.3: List ────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all snapshot blob names sorted by timestamp (oldest → newest).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBlobNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        await foreach (var name in _blobs.ListAsync(BlobPaths.Snapshots, cancellationToken))
            names.Add(name);

        // Sort by the timestamp encoded in the blob name
        names.Sort((a, b) =>
        {
            var ta = ParseTimestamp(a);
            var tb = ParseTimestamp(b);
            return ta.CompareTo(tb);
        });

        return names;
    }

    // ── Task 6.4: Resolve ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a snapshot: returns the latest if <paramref name="version"/> is <c>null</c>,
    /// otherwise returns the snapshot whose timestamp starts with the given version string.
    /// Returns <c>null</c> if no matching snapshot exists.
    /// </summary>
    public async Task<SnapshotManifest?> ResolveAsync(
        string?           version           = null,
        CancellationToken cancellationToken = default)
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
            // Find first blob name whose timestamp portion starts with the version string
            var match = names.LastOrDefault(n =>
            {
                var ts = n.StartsWith(BlobPaths.Snapshots) ? n[BlobPaths.Snapshots.Length..] : n;
                return ts.StartsWith(version, StringComparison.OrdinalIgnoreCase);
            });
            if (match is null) return null;
            blobName = match;
        }

        return await LoadAsync(blobName, cancellationToken);
    }

    // ── Internal: download and deserialize ────────────────────────────────────

    private async Task<SnapshotManifest> LoadAsync(
        string blobName, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SnapshotSerializer.DeserializeAsync(ms.ToArray(), _encryption, cancellationToken);
    }

    // ── Version helper ────────────────────────────────────────────────────────

    private static string GetAriusVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
}
