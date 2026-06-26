using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Snapshot;

public interface ISnapshotService
{
    /// <summary>
    /// Creates a new snapshot: writes plain JSON to disk first (write-through),
    /// then uploads (gzip + optional encrypt) to Azure.
    /// Returns the created manifest.
    /// <para>
    /// <paramref name="overwrite"/> defaults to <c>false</c> (snapshots are append-only in normal operation,
    /// where each run gets a fresh <c>now</c> timestamp). Pass <c>true</c> when re-creating a snapshot at a
    /// deterministic <paramref name="timestamp"/> should be idempotent (e.g. re-running the v5 migration).
    /// </para>
    /// </summary>
    Task<SnapshotManifest> CreateAsync(
        FileTreeHash      rootHash,
        long              fileCount,
        long              originalSize,
        DateTimeOffset?   timestamp         = null,
        bool              overwrite         = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all snapshot blob names sorted by timestamp (oldest → newest).
    /// </summary>
    Task<IReadOnlyList<RelativePath>> ListBlobNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a snapshot: disk-first (reads plain JSON if cached locally), falls back to Azure.
    /// Returns the latest if <paramref name="version"/> is <c>null</c>,
    /// otherwise returns the snapshot whose timestamp starts with the given version string.
    /// Returns <c>null</c> if no matching snapshot exists.
    /// </summary>
    Task<SnapshotManifest?> ResolveAsync(string? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the version identifier for a snapshot blob name — the timestamp filename, which is
    /// exactly what <see cref="ResolveAsync"/> (and ListQueryOptions.Version) match against.
    /// </summary>
    string GetVersion(RelativePath blobName);
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
[SharedWithinAssembly]
internal sealed class SnapshotService : ISnapshotService
{
    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly ICompressionService   _compression;
    private readonly RelativeFileSystem    _diskCacheFileSystem;

    /// <summary>
    /// Timestamp format used for snapshot blob names and local cache filenames.
    /// Lexicographic sort == chronological sort.
    /// e.g. <c>2026-03-22T150000.000Z</c>
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-ddTHHmmss.fffZ";

    private static readonly JsonSerializerOptions SerializerOptions = new()
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
        ICompressionService   compression,
        string                accountName,
        string                containerName)
    {
        _blobs        = blobs;
        _encryption   = encryption;
        _compression  = compression;
        var diskCacheRoot = RepositoryLocalStatePaths.GetSnapshotCacheRoot(accountName, containerName);
        _diskCacheFileSystem = new RelativeFileSystem(diskCacheRoot);
        _diskCacheFileSystem.CreateDirectory(RelativePath.Root);
    }

    public static DateTimeOffset ParseTimestamp(RelativePath blobName)
    {
        var name = GetSnapshotFileName(blobName);

        return DateTimeOffset.ParseExact(name.ToString(), TimestampFormat, null,
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
        long              originalSize,
        DateTimeOffset?   timestamp         = null,
        bool              overwrite         = false,
        CancellationToken cancellationToken = default)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = fileCount,
            OriginalSize = originalSize,
            AriusVersion = AriusVersion.Informational
        };

        // Write-through: disk first (plain JSON)
        await WriteToDiskAsync(manifest, cancellationToken);

        // Then Azure (compress + optional encrypt)
        var bytes    = await SnapshotSerializer.SerializeAsync(manifest, _encryption, _compression, cancellationToken);
        var blobName = BlobPaths.SnapshotPath(ts);

        await _blobs.UploadAsync(
            blobName,
            new MemoryStream(bytes),
            new Dictionary<string, string>(),
            BlobTier.Cool,
            _encryption.IsEncrypted ? ContentTypes.SnapshotGcmEncrypted : ContentTypes.SnapshotPlaintext,
            overwrite: overwrite,
            cancellationToken: cancellationToken);

        return manifest;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all snapshot blob names sorted by timestamp (oldest → newest).
    /// </summary>
    public async Task<IReadOnlyList<RelativePath>> ListBlobNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<RelativePath>();
        await foreach (var item in _blobs.ListAsync(BlobPaths.SnapshotsPrefix, includeMetadata: false, cancellationToken: cancellationToken))
            names.Add(item.Name);

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

        RelativePath blobName;
        if (version is null)
        {
            blobName = names[^1]; // latest (last after sort)
        }
        else
        {
            var foundMatch = false;
            var match = default(RelativePath);
            foreach (var candidate in names)
            {
                if (!GetSnapshotFileName(candidate).StartsWith(version, StringComparison.OrdinalIgnoreCase))
                    continue;

                match = candidate;
                foundMatch = true;
            }

            if (!foundMatch)
                return null;

            blobName = match;
        }

        // Disk-first: check local plain-JSON cache
        var localPath = GetDiskCachePath(blobName);
        if (_diskCacheFileSystem.FileExists(localPath))
        {
            var json = await _diskCacheFileSystem.ReadAllBytesAsync(localPath, cancellationToken);
            return JsonSerializer.Deserialize<SnapshotManifest>(json, SerializerOptions)
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
        var path = RelativePath.Root / PathSegment.Parse(manifest.Timestamp.UtcDateTime.ToString(TimestampFormat));
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        await _diskCacheFileSystem.WriteAllBytesAsync(path, json, cancellationToken);
    }

    private async Task<SnapshotManifest> LoadFromAzureAsync(
        RelativePath blobName, CancellationToken cancellationToken)
    {
        var download = await _blobs.DownloadAsync(blobName, cancellationToken);
        await using var stream = download.Stream;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SnapshotSerializer.DeserializeAsync(ms.ToArray(), _encryption, _compression, cancellationToken);
    }

    public string GetVersion(RelativePath blobName) => GetSnapshotFileName(blobName).ToString();

    private static PathSegment GetSnapshotFileName(RelativePath blobName) =>
        blobName.Parent is { } parent && (parent == RelativePath.Root || parent == BlobPaths.SnapshotsPrefix)
            ? blobName.Name
            : throw new FormatException($"Invalid snapshot blob name: '{blobName}'.");

    private static RelativePath GetDiskCachePath(RelativePath blobName) => RelativePath.Root / GetSnapshotFileName(blobName);
}
