using System.Formats.Tar;
using System.Globalization;
using System.Text;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Migration;

/// <summary>
/// Migrates a v5 Arius repository to v7, in place. Stages:
/// <list type="number">
///   <item>── Stage 1: Read the v5 SQLite state DB (download → decrypt → gunzip → temp .sqlite).</item>
///   <item>── Stage 2: Load + classify BinaryProperties (large/tar/thin); enumerate chunk blobs.</item>
///   <item>── Stage 3: Upsert v7 metadata onto chunk blobs; create empty thin stubs.</item>
///   <item>── Stage 4: Rebuild the chunk index via the existing repair functionality.</item>
///   <item>── Stage 5: Build the v7 filetrees from PointerFileEntries.</item>
///   <item>── Stage 6: Create the snapshot and promote the chunk-index version.</item>
/// </list>
/// Chunk bodies are never touched: v5 hashes (salted SHA-256), AES-256-CBC encryption and gzip
/// compression are already readable by v7. Metadata is UPSERTed (merged), so v5 keys such as
/// SmallChunkCount are preserved and the migrated repo stays readable by v5 too.
/// </summary>
internal sealed class MigrateV5
{
    private const string PointerSuffix = ".pointer.arius";

    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService    _encryption;
    private readonly ICompressionService   _compression;
    private readonly IChunkStorageService  _chunkStorage;
    private readonly IChunkIndexService    _chunkIndex;
    private readonly IFileTreeService      _fileTreeService;
    private readonly ISnapshotService      _snapshots;
    private readonly ILoggerFactory        _loggerFactory;
    private readonly ILogger<MigrateV5>    _logger;
    private readonly string  _account;
    private readonly string  _container;
    private readonly string? _passphrase;

    public MigrateV5(IServiceProvider provider, string account, string container, string? passphrase)
    {
        _blobs           = provider.GetRequiredService<IBlobContainerService>();
        _encryption      = provider.GetRequiredService<IEncryptionService>();
        _compression     = provider.GetRequiredService<ICompressionService>();
        _chunkStorage    = provider.GetRequiredService<IChunkStorageService>();
        _chunkIndex      = provider.GetRequiredService<IChunkIndexService>();
        _fileTreeService = provider.GetRequiredService<IFileTreeService>();
        _snapshots       = provider.GetRequiredService<ISnapshotService>();
        _loggerFactory   = provider.GetRequiredService<ILoggerFactory>();
        _logger          = _loggerFactory.CreateLogger<MigrateV5>();
        _account         = account;
        _container       = container;
        _passphrase      = passphrase;
    }

    public async Task RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        // v5 derived chunk hashes and encryption keys from the ASCII bytes of the passphrase; v7 uses
        // UTF-8. They coincide only for ASCII passphrases — otherwise hashes/keys diverge silently.
        if (_passphrase is not null && !Ascii.IsValid(_passphrase))
            throw new NotSupportedException("Migration requires an ASCII passphrase (v5 keys/hashes are derived from ASCII bytes).");

        var dbPath = await DownloadStateDbAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var (binaries, pointers) = LoadState(connection);
            var (large, tars, thins) = Classify(binaries);
            var chunkBlobs = await EnumerateChunkBlobsAsync(cancellationToken);

            _logger.LogInformation("v5 state: {Binaries} binaries ({Large} large, {Tar} tar, {Thin} thin), {Pointers} pointer entries; {Blobs} chunk blobs in storage",
                binaries.Count, large.Count, tars.Count, thins.Count, pointers.Count, chunkBlobs.Count);

            //await ValidateTarEntryNamesAsync(tars, cancellationToken);

            if (dryRun)
            {
                DumpSchema(connection, pointers);
                _logger.LogInformation("[dry-run] No changes written.");
                return;
            }

            await UpsertChunkMetadataAsync(large, tars, thins, chunkBlobs, cancellationToken);
            await RebuildChunkIndexAsync(cancellationToken);
            await BuildAndSnapshotAsync(pointers, binaries, cancellationToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the file handle before deleting
            TryDelete(dbPath);
        }
    }

    // ── Stage 1: Read the v5 state DB ───────────────────────────────────────────────

    private async Task<string> DownloadStateDbAsync(CancellationToken cancellationToken)
    {
        // The v5 state DB lives at "states/<name>" (no v7 BlobPaths constant). "Latest" is the
        // lexicographically-greatest name (timestamps sort chronologically).
        var statesPrefix = RelativePath.Parse("states");
        BlobListItem? latest = null;
        await foreach (var item in _blobs.ListAsync(statesPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            if (latest is null || string.CompareOrdinal(item.Name.ToString(), latest.Name.ToString()) > 0)
                latest = item;
        }

        if (latest is null)
            throw new InvalidOperationException("No v5 state blob found under 'states/'. Is this a v5 repository?");

        if (latest.Metadata is { } meta && meta.TryGetValue("DatabaseVersion", out var version) && version != "5")
            _logger.LogWarning("State blob DatabaseVersion is '{Version}', expected '5'. Proceeding anyway.", version);

        _logger.LogInformation("── Stage 1: reading v5 state DB '{Name}'", latest.Name);

        var dbPath = Path.Combine(Path.GetTempPath(), $"arius-v5-{Guid.NewGuid():N}.sqlite");
        var download = await _blobs.DownloadAsync(latest.Name, cancellationToken);

        // Stored as encrypt(gzip(sqlite)) — reverse: decrypt (auto-detects Salted__) then decompress
        // (auto-detects gzip). Both read paths are self-describing.
        await using (download.Stream)
        await using (var decrypted = _encryption.WrapForDecryption(download.Stream))
        await using (var decompressed = _compression.WrapForDecompression(decrypted))
        await using (var file = File.Create(dbPath))
            await decompressed.CopyToAsync(file, cancellationToken);

        return dbPath;
    }

    // ── Stage 2: Load + classify ────────────────────────────────────────────────────

    private (List<BinaryRow> Binaries, List<PointerRow> Pointers) LoadState(SqliteConnection connection)
    {
        var binaries = new List<BinaryRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Hash, OriginalSize, ParentHash FROM BinaryProperties";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                binaries.Add(new BinaryRow(
                    Hash:         (byte[])reader["Hash"],
                    OriginalSize: reader.GetInt64(1),
                    ParentHash:   reader.IsDBNull(2) ? null : (byte[])reader["ParentHash"]));
            }
        }

        var pointers = new List<PointerRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT RelativeName, Hash, CreationTimeUtc, LastWriteTimeUtc FROM PointerFileEntries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue; // orphan pointer — nothing to point at
                pointers.Add(new PointerRow(
                    RelativeName: reader.GetString(0),
                    Hash:         (byte[])reader["Hash"],
                    Created:      ReadTimestamp(reader, 2),
                    Modified:     ReadTimestamp(reader, 3)));
            }
        }

        return (binaries, pointers);
    }

    private static (List<BinaryRow> Large, List<BinaryRow> Tars, List<BinaryRow> Thins) Classify(List<BinaryRow> binaries)
    {
        // A small file (thin) has a non-null ParentHash. A tar is a parent of some thin. Everything
        // else with no parent is a standalone large chunk.
        var parentHashes = binaries
            .Where(b => b.ParentHash is not null)
            .Select(b => ToHex(b.ParentHash!))
            .ToHashSet();

        var large = new List<BinaryRow>();
        var tars  = new List<BinaryRow>();
        var thins = new List<BinaryRow>();
        foreach (var b in binaries)
        {
            if (b.ParentHash is not null)            thins.Add(b);
            else if (parentHashes.Contains(ToHex(b.Hash))) tars.Add(b);
            else                                     large.Add(b);
        }

        return (large, tars, thins);
    }

    private async Task<Dictionary<string, ChunkBlob>> EnumerateChunkBlobsAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, ChunkBlob>(StringComparer.Ordinal);
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            var hash = item.Name.Name.ToString(); // last path segment is the hash
            map[hash] = new ChunkBlob(
                Length:   item.ContentLength ?? 0,
                Tier:     item.Tier ?? BlobTier.Cool,
                Metadata: item.Metadata ?? new Dictionary<string, string>());
        }

        return map;
    }

    //private async Task ValidateTarEntryNamesAsync(List<BinaryRow> tars, CancellationToken cancellationToken)
    //{
    //    // v7 restore requires every tar entry name to parse as a ContentHash. v5 names entries by
    //    // hash.ToString() (hex), so this should always hold — but verify one tar up front rather than
    //    // discover an un-restorable repo only at restore time.
    //    if (tars.Count == 0)
    //        return;

    //    var tarHash = ChunkHash.FromDigest(tars[0].Hash);
    //    await using var stream = await _chunkStorage.DownloadAsync(tarHash, cancellationToken: cancellationToken);
    //    await using var tarReader = new TarReaderAdapter(stream);

    //    var entries = 0;
    //    while (await tarReader.GetNextEntryAsync(cancellationToken) is { } name)
    //    {
    //        if (!ContentHash.TryParse(name, out _))
    //            throw new InvalidDataException(
    //                $"Tar chunk {tarHash.Short8} has entry '{name}' that is not a content hash; this v5 repo cannot be restored by v7.");
    //        entries++;
    //    }

    //    _logger.LogInformation("── Stage 2: validated {Entries} entry name(s) in tar {Tar} parse as content hashes", entries, tarHash.Short8);
    //}

    // ── Stage 3: Upsert chunk metadata ──────────────────────────────────────────────

    private async Task UpsertChunkMetadataAsync(
        List<BinaryRow> large, List<BinaryRow> tars, List<BinaryRow> thins,
        Dictionary<string, ChunkBlob> chunkBlobs, CancellationToken cancellationToken)
    {
        _logger.LogInformation("── Stage 3: upserting chunk metadata ({Large} large, {Tar} tar, {Thin} thin)", large.Count, tars.Count, thins.Count);

        foreach (var b in large)
        {
            var hex = ToHex(b.Hash);
            if (!chunkBlobs.TryGetValue(hex, out var blob))
            {
                _logger.LogWarning("Large chunk blob missing for {Hash}; skipping.", hex[..8]);
                continue;
            }

            var descriptor = new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = b.OriginalSize.ToString(CultureInfo.InvariantCulture),
                [BlobMetadataKeys.ChunkSize]    = blob.Length.ToString(CultureInfo.InvariantCulture),
            };
            await WriteDescriptorAsync(b.Hash, blob, descriptor, cancellationToken);
        }

        foreach (var b in tars)
        {
            var hex = ToHex(b.Hash);
            if (!chunkBlobs.TryGetValue(hex, out var blob))
            {
                _logger.LogWarning("Tar chunk blob missing for {Hash}; skipping.", hex[..8]);
                continue;
            }

            // No original_size on tar blobs — the per-file sizes live on the thin chunks.
            var descriptor = new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar,
                [BlobMetadataKeys.ChunkSize] = blob.Length.ToString(CultureInfo.InvariantCulture),
            };
            await WriteDescriptorAsync(b.Hash, blob, descriptor, cancellationToken);
        }

        foreach (var b in thins)
        {
            var parentHex = ToHex(b.ParentHash!);
            if (!chunkBlobs.TryGetValue(parentHex, out var parent))
            {
                _logger.LogWarning("Parent tar {Tar} missing for thin chunk {Hash}; skipping.", parentHex[..8], ToHex(b.Hash)[..8]);
                continue;
            }

            // Creates an empty stub blob at chunks/<fileHash> carrying the thin metadata; idempotent.
            // Thin stubs are always created fresh at Cool tier, so their own metadata is always writable —
            // they never need a descriptor sidecar.
            await _chunkStorage.UploadThinAsync(
                ContentHash.FromDigest(b.Hash),
                ChunkHash.FromDigest(b.ParentHash!),
                b.OriginalSize,
                parent.Length,
                cancellationToken);
        }

        return;

        // Writes the v7 chunk descriptor. Non-archived blobs take the merged metadata (so v5 keys such as
        // SmallChunkCount survive and the repo stays v5-readable). Archived blobs forbid Set Blob Metadata
        // (409 BlobArchived), so for those we write the same descriptor to a zero-byte Cool sidecar at
        // chunk-descriptors/<hash>; chunk-index repair reads it as a fallback. overwrite:true keeps the
        // migration idempotent across re-runs.
        async Task WriteDescriptorAsync(byte[] hash, ChunkBlob blob, Dictionary<string, string> descriptor, CancellationToken ct)
        {
            var chunkHash = ChunkHash.FromDigest(hash);
            if (blob.Tier == BlobTier.Archive)
                await _blobs.UploadAsync(
                    blobName:          BlobPaths.ChunkDescriptorPath(chunkHash),
                    content:           new MemoryStream([], writable: false),
                    metadata:          descriptor,
                    tier:              BlobTier.Cool,
                    overwrite:         true,
                    cancellationToken: ct);
            else
                await _blobs.SetMetadataAsync(BlobPaths.ChunkPath(chunkHash), Merge(blob.Metadata, descriptor), ct);
        }
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> existing, Dictionary<string, string> updates)
    {
        var merged = new Dictionary<string, string>(existing);
        foreach (var (k, v) in updates)
            merged[k] = v;
        return merged;
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return DateTimeOffset.UnixEpoch; // deterministic sentinel for missing v5 timestamps

        return reader.GetValue(ordinal) switch
        {
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) => dto,
            long ticks  => new DateTimeOffset(ticks, TimeSpan.Zero),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _           => DateTimeOffset.UnixEpoch,
        };
    }

    // ── Stage 4: Rebuild chunk index ────────────────────────────────────────────────

    private async Task RebuildChunkIndexAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("── Stage 4: rebuilding chunk index (repair)");
        var result = await _chunkIndex.RepairAsync(cancellationToken);
        _logger.LogInformation("Chunk index repaired: {Result}", result);
    }

    // ── Stages 5 & 6: Build filetrees, snapshot, promote ────────────────────────────

    private async Task BuildAndSnapshotAsync(List<PointerRow> pointers, List<BinaryRow> binaries, CancellationToken cancellationToken)
    {
        _logger.LogInformation("── Stage 5: building filetrees from {Count} pointer entries", pointers.Count);

        var sizeByHash = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var b in binaries)
            sizeByHash[ToHex(b.Hash)] = b.OriginalSize;

        var             cacheRoot = RepositoryLocalStatePaths.GetFileTreeCacheRoot(_account, _container);
        await using var session   = await FileTreeStagingSession.OpenAsync(cacheRoot, cancellationToken);
        using var       writer    = new FileTreeStagingWriter(session.StagingRoot);

        long fileCount = 0, totalSize = 0, skipped = 0;
        foreach (var p in pointers)
        {
            if (!TryToBinaryPath(p.RelativeName, out var relPath))
            {
                _logger.LogWarning("Skipping pointer entry with unusable path '{Path}'.", p.RelativeName);
                skipped++;
                continue;
            }

            await writer.AppendFileEntryAsync(relPath, ContentHash.FromDigest(p.Hash), p.Created, p.Modified, cancellationToken);
            fileCount++;
            totalSize += sizeByHash.GetValueOrDefault(ToHex(p.Hash), 0);
        }

        if (skipped > 0)
            _logger.LogWarning("{Skipped} pointer entries skipped (unusable paths).", skipped);

        // ValidateAsync must run once before the builder calls ExistsInRemote (mirrors archive stage 6a).
        await _fileTreeService.ValidateAsync(cancellationToken);

        var builder = new FileTreeBuilder(_encryption, _fileTreeService, _loggerFactory.CreateLogger<FileTreeBuilder>());
        var rootHash = await builder.SynchronizeAsync(session.StagingRoot, cancellationToken);
        if (rootHash is null)
        {
            _logger.LogWarning("No file entries — nothing to snapshot. Migration complete.");
            return;
        }

        _logger.LogInformation("── Stage 6: creating snapshot ({Files} files, {Bytes} bytes)", fileCount, totalSize);
        var snapshot = await _snapshots.CreateAsync(rootHash.Value, fileCount, totalSize, cancellationToken: cancellationToken);
        await _chunkIndex.PromoteToSnapshotVersionAsync(BlobPaths.SnapshotPath(snapshot.Timestamp).Name.ToString());

        _logger.LogInformation("Migration complete. Snapshot {Timestamp} created with {Files} files.", snapshot.Timestamp, fileCount);
    }

    private static bool TryToBinaryPath(string relativeName, out RelativePath path)
    {
        // v5 stores forward-slash paths, but be defensive about Windows-origin backslashes
        // (RelativePath.Parse rejects them, and FromPlatformRelativePath only converts the OS separator).
        var normalized = relativeName.Replace('\\', '/');
        if (!RelativePath.TryParse(normalized, out path))
            return false;

        if (path.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            path = path.RemoveSuffix(PointerSuffix, StringComparison.OrdinalIgnoreCase);

        return path != RelativePath.Root;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private void DumpSchema(SqliteConnection connection, List<PointerRow> pointers)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _logger.LogInformation("[dry-run] table {Name}: {Sql}", reader.GetString(0), reader.GetString(1));

        foreach (var p in pointers.Take(10))
            _logger.LogInformation("[dry-run] pointer '{Name}' → {Hash} (c={Created:o}, m={Modified:o})",
                p.RelativeName, ToHex(p.Hash)[..8], p.Created, p.Modified);
    }

    private sealed record BinaryRow(byte[] Hash, long OriginalSize, byte[]? ParentHash);
    private sealed record PointerRow(string RelativeName, byte[] Hash, DateTimeOffset Created, DateTimeOffset Modified);
    private sealed record ChunkBlob(long Length, BlobTier Tier, IReadOnlyDictionary<string, string> Metadata);
}
