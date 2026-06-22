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
///   <item>── Stage 1: Read the latest v5 SQLite state DB (download → decrypt → gunzip → temp .sqlite).</item>
///   <item>── Stage 2: Load + classify BinaryProperties (large/tar/thin); enumerate chunk blobs.</item>
///   <item>── Stage 3: Upsert v7 metadata onto chunk blobs; create empty thin stubs.</item>
///   <item>── Stage 4: Rebuild the chunk index via the existing repair functionality.</item>
///   <item>── Stage 5: Build the v7 filetrees from PointerFileEntries.</item>
///   <item>── Stage 6: Create the snapshot and promote the chunk-index version.</item>
///   <item>── Stage 7: Turn every OLDER state DB under 'states/' into its own historical snapshot.</item>
/// </list>
/// Chunk bodies are never touched: v5 hashes (salted SHA-256), AES-256-CBC encryption and gzip
/// compression are already readable by v7. Metadata is UPSERTed (merged), so v5 keys such as
/// SmallChunkCount are preserved and the migrated repo stays readable by v5 too.
/// <para>
/// Stage 7 reconstructs the historical timeline: every state blob under 'states/' (the legacy v3
/// 'ChunkEntries' schema and the v5 'BinaryProperties' schema are both supported) becomes one snapshot
/// at that state's own version. It reuses the chunk index rebuilt in Stage 4 (older states reference a
/// subset of the latest state's chunks) and does NOT re-upsert metadata or promote the index — the
/// historical snapshots are older than the latest, which already owns the promoted index version.
/// </para>
/// </summary>
internal sealed class MigrateV5
{
    private const string PointerSuffix = ".pointer.arius";

    // Stage 3 issues one small blob op per chunk; fan them out (matches the codebase's blob-fanout degree).
    private const int UpsertWorkers = 32;

    // v5 names its state-DB blob "states/<yyyy-MM-ddTHH-mm-ss.fff>" (UTC). The migration reuses that instant
    // as the v7 snapshot timestamp, so the snapshot is named after the v5 state it was built from. The colon
    // variants accept the legacy v3 file-name style (e.g. "2025-11-26T15:27:33") used by --extra-states files.
    private static readonly string[] V5StateTimestampFormats =
        ["yyyy-MM-ddTHH-mm-ss.fff", "yyyy-MM-ddTHH-mm-ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss"];

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

        var (dbPath, snapshotTimestamp, otherStates) = await DownloadStateDbAsync(cancellationToken);
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
                await MigrateOtherStatesAsync(snapshotTimestamp, otherStates, dryRun: true, cancellationToken);
                _logger.LogInformation("[dry-run] No changes written.");
                return;
            }

            await UpsertChunkMetadataAsync(large, tars, thins, chunkBlobs, cancellationToken);
            await RebuildChunkIndexAsync(cancellationToken);
            await BuildAndSnapshotAsync(pointers, binaries, snapshotTimestamp, cancellationToken);
            await MigrateOtherStatesAsync(snapshotTimestamp, otherStates, dryRun: false, cancellationToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the file handle before deleting
            TryDelete(dbPath);
        }
    }

    // ── Stage 1: Read the v5 state DB ───────────────────────────────────────────────

    private async Task<(string DbPath, DateTimeOffset SnapshotTimestamp, IReadOnlyList<BlobListItem> OtherStates)> DownloadStateDbAsync(CancellationToken cancellationToken)
    {
        // The v5/v3 state DBs live at "states/<name>" (no v7 BlobPaths constant), one blob per historical run.
        var statesPrefix = RelativePath.Parse("states");
        var states = new List<BlobListItem>();
        await foreach (var item in _blobs.ListAsync(statesPrefix, includeMetadata: true, cancellationToken: cancellationToken))
            states.Add(item);

        if (states.Count == 0)
            throw new InvalidOperationException("No v5 state blob found under 'states/'. Is this a v5 repository?");

        // "Latest" is the chronologically-greatest state. The blob-name timestamp parses for both the legacy
        // v3 ':' style and the v5 '-' style; CompareStateBlobs falls back to ordinal name order otherwise.
        states.Sort(CompareStateBlobs);
        var latest      = states[^1];
        var otherStates = states.GetRange(0, states.Count - 1);

        if (latest.Metadata is { } meta && meta.TryGetValue("DatabaseVersion", out var version) && version != "5")
            _logger.LogWarning("Latest state blob DatabaseVersion is '{Version}', expected '5'. Proceeding anyway.", version);

        var snapshotTimestamp = ResolveSnapshotTimestamp(latest.Name);
        _logger.LogInformation("── Stage 1: reading latest state DB '{Name}' (snapshot {Timestamp:o}); {Others} older state(s) queued for Stage 7",
            latest.Name, snapshotTimestamp, otherStates.Count);

        var dbPath = await DownloadAndDecryptStateAsync(latest.Name, cancellationToken);
        return (dbPath, snapshotTimestamp, otherStates);
    }

    /// <summary>Downloads a "states/" blob and reverses encrypt(gzip(sqlite)) into a temp .sqlite file; returns its path.</summary>
    private async Task<string> DownloadAndDecryptStateAsync(RelativePath blobName, CancellationToken cancellationToken)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arius-v5-{Guid.NewGuid():N}.sqlite");
        try
        {
            var download = await _blobs.DownloadAsync(blobName, cancellationToken);

            // Stored as encrypt(gzip(sqlite)) — reverse: decrypt (auto-detects Salted__) then decompress
            // (auto-detects gzip). Both read paths are self-describing.
            await using (download.Stream)
            await using (var decrypted = _encryption.WrapForDecryption(download.Stream))
            await using (var decompressed = _compression.WrapForDecompression(decrypted))
            await using (var file = File.Create(dbPath))
                await decompressed.CopyToAsync(file, cancellationToken);

            return dbPath;
        }
        catch
        {
            // A failed decrypt/decompress (corrupt body, wrong passphrase, truncation, cancellation) leaves a
            // partial temp file the callers can't see to clean up — delete it before propagating.
            TryDelete(dbPath);
            throw;
        }
    }

    // Total order over state blobs: parseable timestamps sort chronologically and after any unparseable
    // names (so the latest is a real, parseable state); unparseable names sort among themselves by ordinal.
    private static int CompareStateBlobs(BlobListItem a, BlobListItem b)
    {
        var aOk = TryParseStateTimestamp(a.Name.Name.ToString(), out var at);
        var bOk = TryParseStateTimestamp(b.Name.Name.ToString(), out var bt);
        if (aOk && bOk) return at.CompareTo(bt);
        if (aOk != bOk) return aOk ? 1 : -1;
        return string.CompareOrdinal(a.Name.ToString(), b.Name.ToString());
    }

    /// <summary>
    /// Parses a state-DB name (e.g. "2026-06-20T18-01-34.131" or the legacy "2025-11-26T15:27:33", interpreted
    /// as UTC) into the instant used to name the v7 snapshot, so the snapshot carries the state's timestamp.
    /// Falls back to the current time if the name isn't a recognizable timestamp.
    /// </summary>
    private DateTimeOffset ResolveSnapshotTimestamp(RelativePath stateName)
    {
        if (TryParseStateTimestamp(stateName.Name.ToString(), out var ts))
            return ts;

        _logger.LogWarning("State DB name '{Name}' is not a parseable timestamp; using the current time for the snapshot.", stateName.Name);
        return DateTimeOffset.UtcNow;
    }

    private static bool TryParseStateTimestamp(string name, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(name, V5StateTimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

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

        // The three groups are independent and run concurrently: each writes a disjoint set of blob names
        // (large/tar by their own hash, thins by their content hash), the parent-tar size a thin needs is read
        // from the in-memory chunkBlobs map (not from storage), and chunkBlobs is read-only here.
        var options = new ParallelOptions { MaxDegreeOfParallelism = UpsertWorkers, CancellationToken = cancellationToken };

        var largeTask = Parallel.ForEachAsync(large, options, async (b, ct) =>
        {
            var hex = ToHex(b.Hash);
            if (!chunkBlobs.TryGetValue(hex, out var blob))
            {
                _logger.LogWarning("Large chunk blob missing for {Hash}; skipping.", hex[..8]);
                return;
            }

            var metadata = new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = b.OriginalSize.ToString(CultureInfo.InvariantCulture),
                [BlobMetadataKeys.ChunkSize]    = blob.Length.ToString(CultureInfo.InvariantCulture),
            };
            await WriteMetadataAsync(b.Hash, blob, metadata, ct);
        });

        var tarTask = Parallel.ForEachAsync(tars, options, async (b, ct) =>
        {
            var hex = ToHex(b.Hash);
            if (!chunkBlobs.TryGetValue(hex, out var blob))
            {
                _logger.LogWarning("Tar chunk blob missing for {Hash}; skipping.", hex[..8]);
                return;
            }

            // No original_size on tar blobs — the per-file sizes live on the thin chunks.
            var metadata = new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar,
                [BlobMetadataKeys.ChunkSize] = blob.Length.ToString(CultureInfo.InvariantCulture),
            };
            await WriteMetadataAsync(b.Hash, blob, metadata, ct);
        });

        var thinTask = Parallel.ForEachAsync(thins, options, async (b, ct) =>
        {
            var parentHex = ToHex(b.ParentHash!);
            if (!chunkBlobs.TryGetValue(parentHex, out var parent))
            {
                _logger.LogWarning("Parent tar {Tar} missing for thin chunk {Hash}; skipping.", parentHex[..8], ToHex(b.Hash)[..8]);
                return;
            }

            await _chunkStorage.UploadThinAsync(
                ContentHash.FromDigest(b.Hash),
                ChunkHash.FromDigest(b.ParentHash!),
                b.OriginalSize,
                parent.Length,
                ct);
        });

        await Task.WhenAll(largeTask, tarTask, thinTask);

        return;

        // Write the metadata
        // For v5 chunks in archive tier we write the metadata to the sidecar (409 BlobArchived otherwise)
        // Otherwise we upsert the metadata on the chunk in-place
        // chunk-index repair reads it as a fallback.
        async Task WriteMetadataAsync(byte[] hash, ChunkBlob blob, Dictionary<string, string> metadata, CancellationToken ct)
        {
            var chunkHash = ChunkHash.FromDigest(hash);
            if (blob.Tier == BlobTier.Archive)
                await _blobs.UploadAsync(
                    blobName:          BlobPaths.V5LegacySideCarPath(chunkHash),
                    content:           new MemoryStream([], writable: false),
                    metadata:          metadata,
                    tier:              BlobTier.Cool,
                    contentType:       ContentTypes.V5LegacyMetadataSideCar,
                    overwrite:         true,
                    cancellationToken: ct);
            else
                await _blobs.SetMetadataAsync(BlobPaths.ChunkPath(chunkHash), Merge(blob.Metadata, metadata), ct);
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

    private async Task BuildAndSnapshotAsync(List<PointerRow> pointers, List<BinaryRow> binaries, DateTimeOffset snapshotTimestamp, CancellationToken cancellationToken)
    {
        _logger.LogInformation("── Stage 5 & 6: building filetree + snapshot for the latest state");

        var snapshot = await BuildAndSnapshotCoreAsync(pointers, BuildSizeByHash(binaries), snapshotTimestamp, cancellationToken);
        if (snapshot is null)
        {
            _logger.LogWarning("No file entries — nothing to snapshot. Migration complete.");
            return;
        }

        // Only the latest snapshot promotes the chunk index: it is the newest version, and the index rebuilt
        // in Stage 4 is validated against it. Historical (older) snapshots in Stage 7 reference a subset and
        // must not re-tag the cache to an older version.
        await _chunkIndex.PromoteToSnapshotVersionAsync(BlobPaths.SnapshotPath(snapshot.Timestamp).Name.ToString());

        _logger.LogInformation("Latest snapshot {Timestamp} created with {Files} files.", snapshot.Timestamp, snapshot.FileCount);
    }

    /// <summary>
    /// Stages the given pointer entries into a filetree, synchronizes it, and creates the snapshot at
    /// <paramref name="snapshotTimestamp"/> (idempotent: <c>overwrite: true</c>, since the timestamp is the
    /// deterministic state version). Returns the created manifest, or <c>null</c> if there were no usable entries.
    /// Shared by the latest-state path (Stage 6) and the historical-state path (Stage 7); it does NOT promote
    /// the chunk-index version — the caller decides that.
    /// </summary>
    private async Task<SnapshotManifest?> BuildAndSnapshotCoreAsync(
        IReadOnlyList<PointerRow>         pointers,
        IReadOnlyDictionary<string, long> sizeByHash,
        DateTimeOffset                    snapshotTimestamp,
        CancellationToken                 cancellationToken)
    {
        _logger.LogInformation("Building filetree from {Count} pointer entries (snapshot {Timestamp:o})", pointers.Count, snapshotTimestamp);

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

        // ValidateAsync must run once before the builder calls ExistsInRemote (mirrors archive stage 6a). It
        // caches its result, so calling it again per historical snapshot is a cheap no-op.
        await _fileTreeService.ValidateAsync(cancellationToken);

        var builder  = new FileTreeBuilder(_encryption, _fileTreeService, _loggerFactory.CreateLogger<FileTreeBuilder>());
        var rootHash = await builder.SynchronizeAsync(session.StagingRoot, cancellationToken);
        if (rootHash is null)
            return null;

        _logger.LogInformation("Creating snapshot {Timestamp:o} ({Files} files, {Bytes} bytes)", snapshotTimestamp, fileCount, totalSize);
        // overwrite: true — the timestamp is deterministic (the state version), so re-running the migration
        // must idempotently rewrite the snapshot rather than fail on an existing blob.
        return await _snapshots.CreateAsync(rootHash.Value, fileCount, totalSize, timestamp: snapshotTimestamp, overwrite: true, cancellationToken: cancellationToken);
    }

    private static Dictionary<string, long> BuildSizeByHash(IEnumerable<BinaryRow> binaries)
    {
        var sizeByHash = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var b in binaries)
            sizeByHash[ToHex(b.Hash)] = b.OriginalSize;
        return sizeByHash;
    }

    // ── Stage 7: Migrate older state DBs into historical snapshots ───────────────────

    /// <summary>
    /// Turns every OLDER state blob (all of "states/" except the latest, which Stage 6 already handled) into
    /// its own snapshot at that state's version. v3 ('ChunkEntries') and v5 ('BinaryProperties') schemas are
    /// both supported. Files that can't be read, have an unrecognized schema, or resolve to the latest
    /// snapshot's timestamp are skipped. Snapshots are deduplicated by timestamp and built oldest → newest.
    /// </summary>
    private async Task MigrateOtherStatesAsync(
        DateTimeOffset               mainSnapshotTimestamp,
        IReadOnlyList<BlobListItem>  otherStates,
        bool                         dryRun,
        CancellationToken            cancellationToken)
    {
        if (otherStates.Count == 0)
            return;

        _logger.LogInformation("── Stage 7: migrating {Count} older state blob(s) into historical snapshots", otherStates.Count);

        var planned = new SortedDictionary<DateTimeOffset, HistoricalSnapshotPlan>();
        foreach (var state in otherStates)
        {
            var name = state.Name.Name.ToString();

            HistoricalSnapshotPlan? plan;
            try
            {
                plan = await LoadStateForSnapshotAsync(state.Name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // never swallow cancellation
            }
            catch (Exception ex)
            {
                // Best-effort: a single unreadable state should not abort the migration (the latest snapshot
                // already succeeded). Surface it loudly so a genuine problem isn't missed.
                _logger.LogWarning("Skipping state '{Name}': could not read it ({Message}).", name, ex.Message);
                continue;
            }

            if (plan is null)
            {
                _logger.LogWarning("Skipping state '{Name}': unrecognized schema (no ChunkEntries or BinaryProperties table).", name);
                continue;
            }

            if (plan.Timestamp == mainSnapshotTimestamp)
            {
                _logger.LogInformation("Skipping state '{Name}': version {Timestamp:o} equals the latest snapshot already created.", name, plan.Timestamp);
                continue;
            }

            if (planned.TryGetValue(plan.Timestamp, out var existing))
            {
                _logger.LogWarning("Skipping state '{Name}': snapshot {Timestamp:o} already planned from '{Other}'.", name, plan.Timestamp, existing.SourceName);
                continue;
            }

            planned[plan.Timestamp] = plan;
            _logger.LogInformation("Planned snapshot {Timestamp:o} from '{Name}' ({Schema} format, {Files} files)", plan.Timestamp, name, plan.Schema, plan.Pointers.Count);
        }

        foreach (var (timestamp, plan) in planned) // SortedDictionary ⇒ oldest → newest
        {
            if (dryRun)
            {
                _logger.LogInformation("[dry-run] would create snapshot {Timestamp:o} ({Files} files) from '{Source}'", timestamp, plan.Pointers.Count, plan.SourceName);
                continue;
            }

            var manifest = await BuildAndSnapshotCoreAsync(plan.Pointers, plan.SizeByHash, timestamp, cancellationToken);
            if (manifest is null)
                _logger.LogWarning("Historical snapshot {Timestamp:o} from '{Source}' had no usable file entries; skipped.", timestamp, plan.SourceName);
            else
                _logger.LogInformation("Created historical snapshot {Timestamp} ({Files} files) from '{Source}'", manifest.Timestamp, manifest.FileCount, plan.SourceName);
        }

        if (!dryRun)
            _logger.LogInformation("── Stage 7 complete: {Count} historical snapshot(s) processed", planned.Count);
    }

    /// <summary>
    /// Downloads + decrypts a "states/" blob, detects its schema, and reconstructs the file set active at that
    /// state's own version (v3: <c>GetCurrentPointerFileEntries</c> at the max VersionUtc, deleted excluded;
    /// v5: the full pointer table). Returns <c>null</c> for an unrecognized schema or an empty v3 history.
    /// </summary>
    private async Task<HistoricalSnapshotPlan?> LoadStateForSnapshotAsync(RelativePath blobName, CancellationToken cancellationToken)
    {
        var name   = blobName.Name.ToString();
        var dbPath = await DownloadAndDecryptStateAsync(blobName, cancellationToken);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            switch (DetectSchema(connection))
            {
                case StateSchema.V5:
                {
                    var (binaries, pointers) = LoadState(connection);
                    if (!TryParseStateTimestamp(name, out var ts))
                        throw new FormatException($"state name '{name}' is not a parseable timestamp");
                    return new HistoricalSnapshotPlan(ts, "v5", name, pointers, BuildSizeByHash(binaries));
                }
                case StateSchema.V3:
                {
                    var (rows, sizeByHash) = LoadV3State(connection);
                    if (rows.Count == 0)
                        return null;
                    var version  = rows.Max(r => r.Version);
                    var pointers = ReconstructV3Current(rows, version);
                    return new HistoricalSnapshotPlan(version, "v3", name, pointers, sizeByHash);
                }
                default:
                    return null;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the file handle before deleting
            TryDelete(dbPath);
        }
    }

    private static StateSchema DetectSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('BinaryProperties','ChunkEntries')";
        using var reader = cmd.ExecuteReader();

        bool hasV5 = false, hasV3 = false;
        while (reader.Read())
        {
            switch (reader.GetString(0))
            {
                case "BinaryProperties": hasV5 = true; break;
                case "ChunkEntries":     hasV3 = true; break;
            }
        }

        // Prefer v5 if both somehow exist; v5 is the richer, current schema.
        return hasV5 ? StateSchema.V5 : hasV3 ? StateSchema.V3 : StateSchema.Unknown;
    }

    /// <summary>Loads the legacy v3 state: ChunkEntries (hash → size) and the versioned PointerFileEntries rows.</summary>
    private (List<V3PointerRow> Rows, Dictionary<string, long> SizeByHash) LoadV3State(SqliteConnection connection)
    {
        var sizeByHash = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Hash, OriginalLength FROM ChunkEntries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                sizeByHash[ToHex((byte[])reader["Hash"])] = reader.GetInt64(1);
        }

        var rows = new List<V3PointerRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT RelativeName, BinaryHashValue, VersionUtc, IsDeleted, CreationTimeUtc, LastWriteTimeUtc FROM PointerFileEntries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue; // orphan pointer — nothing to point at
                rows.Add(new V3PointerRow(
                    RelativeName: reader.GetString(0),
                    Hash:         (byte[])reader["BinaryHashValue"],
                    Version:      ReadTimestamp(reader, 2),
                    IsDeleted:    !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                    Created:      ReadTimestamp(reader, 4),
                    Modified:     ReadTimestamp(reader, 5)));
            }
        }

        return (rows, sizeByHash);
    }

    /// <summary>
    /// Reconstructs the v3 "current" file set at <paramref name="version"/>: for each path, the latest entry
    /// at or before that version, excluding the ones whose latest state is deleted. Mirrors v3
    /// <c>GetCurrentPointerFileEntriesAsync(includeDeleted: false)</c>.
    /// </summary>
    private static List<PointerRow> ReconstructV3Current(IReadOnlyList<V3PointerRow> rows, DateTimeOffset version) =>
        rows.Where(r => r.Version <= version)
            .GroupBy(r => r.RelativeName, StringComparer.Ordinal)
            // Deterministic latest-wins: newest version first; on an exact-version tie (only possible with
            // malformed data — the v3 PK is (BinaryHashValue, RelativeName, VersionUtc)) prefer the live entry
            // and then order by hash, so the result never depends on (unspecified) SQLite row order.
            .Select(g => g.OrderByDescending(r => r.Version)
                          .ThenBy(r => r.IsDeleted)
                          .ThenBy(r => Convert.ToHexString(r.Hash), StringComparer.Ordinal)
                          .First())
            .Where(r => !r.IsDeleted)
            .Select(r => new PointerRow(r.RelativeName, r.Hash, r.Created, r.Modified))
            .ToList();

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

    // ── Stage 7 types ───────────────────────────────────────────────────────────────

    private enum StateSchema { Unknown, V3, V5 }

    /// <summary>A single versioned v3 PointerFileEntries row (the v3 manifest is append-only and keeps history).</summary>
    private sealed record V3PointerRow(string RelativeName, byte[] Hash, DateTimeOffset Version, bool IsDeleted, DateTimeOffset Created, DateTimeOffset Modified);

    /// <summary>One historical snapshot to create from an older state blob, resolved and deduplicated by timestamp.</summary>
    private sealed record HistoricalSnapshotPlan(
        DateTimeOffset                    Timestamp,
        string                            Schema,
        string                            SourceName,
        IReadOnlyList<PointerRow>         Pointers,
        IReadOnlyDictionary<string, long> SizeByHash);
}
