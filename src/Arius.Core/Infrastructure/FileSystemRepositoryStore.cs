using System.Security.Cryptography;
using System.Text.Json;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

public sealed class FileSystemRepositoryStore
{
    private const int RepoVersion = 1;

    // ── Directory layout ─────────────────────────────────────────────────────
    // {repoPath}/config.json
    // {repoPath}/keys/           — key files
    // {repoPath}/snapshots/      — snapshot JSON files
    // {repoPath}/packs/          — encrypted .pack files
    // {repoPath}/index/          — index JSON files (one per snapshot)

    public async ValueTask<(RepoId RepoId, string ConfigPath, string KeyPath)> InitAsync(
        string repoPath,
        string passphrase,
        long packSize,
        int chunkMin,
        int chunkAvg,
        int chunkMax,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(repoPath);
        Directory.CreateDirectory(Path.Combine(repoPath, "snapshots"));
        Directory.CreateDirectory(Path.Combine(repoPath, "keys"));
        Directory.CreateDirectory(Path.Combine(repoPath, "packs"));
        Directory.CreateDirectory(Path.Combine(repoPath, "index"));

        var repoConfig = new RepoConfig(
            RepoId.New(),
            RepoVersion,
            RandomNumberGenerator.GetInt32(int.MaxValue),
            packSize,
            chunkMin,
            chunkAvg,
            chunkMax);

        var configPath = Path.Combine(repoPath, "config.json");

        await using (var configStream = File.Create(configPath))
        {
            await JsonSerializer.SerializeAsync(configStream, repoConfig, JsonDefaults.Options, cancellationToken);
        }

        // Create the first key file (generates and encrypts the master key)
        var keyManager = new KeyManager(repoPath);
        var (_, keyPath) = await keyManager.CreateFirstKeyAsync(passphrase, cancellationToken);

        return (repoConfig.RepoId, configPath, keyPath);
    }

    public async ValueTask<bool> ValidatePassphraseAsync(
        string repoPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        var keyManager = new KeyManager(repoPath);
        var masterKey  = await keyManager.TryUnlockAsync(passphrase, cancellationToken);
        return masterKey is not null;
    }

    /// <summary>
    /// Unlocks the master key for the given passphrase.
    /// Throws <see cref="InvalidOperationException"/> if the passphrase is wrong.
    /// </summary>
    private static async Task<byte[]> LoadMasterKeyAsync(
        string repoPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var keyManager = new KeyManager(repoPath);
        return await keyManager.TryUnlockAsync(passphrase, cancellationToken)
            ?? throw new InvalidOperationException("Invalid passphrase.");
    }

    // ── Config ───────────────────────────────────────────────────────────────

    private static async Task<RepoConfig> LoadConfigAsync(string repoPath, CancellationToken ct)
    {
        var configPath = Path.Combine(repoPath, "config.json");
        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<RepoConfig>(stream, JsonDefaults.Options, ct)
            ?? throw new InvalidOperationException("Failed to read repo config.");
    }

    // ── Index ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the merged blob→pack index from all index files in {repoPath}/index/.
    /// Returns a dictionary: blobHash.Value → IndexEntry
    /// </summary>
    private static async Task<Dictionary<string, IndexEntry>> LoadIndexAsync(
        string repoPath,
        CancellationToken ct)
    {
        var indexRoot = Path.Combine(repoPath, "index");
        var merged    = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        if (!Directory.Exists(indexRoot))
            return merged;

        foreach (var indexFile in Directory.EnumerateFiles(indexRoot, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(indexFile);
            var entries = await JsonSerializer.DeserializeAsync<IndexEntry[]>(
                stream, JsonDefaults.Options, ct);
            if (entries is null) continue;
            foreach (var e in entries)
                merged[e.BlobHash.Value] = e;
        }

        return merged;
    }

    /// <summary>
    /// Persists index entries for a given snapshot into {repoPath}/index/{snapshotId}.json.
    /// </summary>
    private static async Task WriteIndexAsync(
        string repoPath,
        SnapshotId snapshotId,
        IEnumerable<IndexEntry> entries,
        CancellationToken ct)
    {
        var indexRoot = Path.Combine(repoPath, "index");
        Directory.CreateDirectory(indexRoot);

        var indexPath = Path.Combine(indexRoot, snapshotId.Value + ".json");
        await using var stream = File.Create(indexPath);
        await JsonSerializer.SerializeAsync(stream, entries.ToArray(), JsonDefaults.Options, ct);
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    public async ValueTask<(Snapshot Snapshot, int StoredFiles, int DeduplicatedFiles)> BackupAsync(
        string repoPath,
        string passphrase,
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken = default)
    {
        var masterKey = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);
        var config    = await LoadConfigAsync(repoPath, cancellationToken);
        var chunker   = GearChunker.FromConfig(config);

        var packsRoot    = Path.Combine(repoPath, "packs");
        var snapshotRoot = Path.Combine(repoPath, "snapshots");
        Directory.CreateDirectory(packsRoot);
        Directory.CreateDirectory(snapshotRoot);

        // Load the existing index to detect already-stored blobs (dedup).
        var existingIndex = await LoadIndexAsync(repoPath, cancellationToken);

        var files         = ExpandInputFiles(inputPaths).ToList();
        var snapshotFiles = new List<BackupSnapshotFile>(files.Count);
        var newEntries    = new List<IndexEntry>();

        var stored      = 0;
        var deduplicated = 0;

        // Track chunk hashes seen in this backup run (in the packer buffer or already sealed).
        // Needed so that two files with identical content in the same run count correctly.
        var seenThisRun = new HashSet<string>(StringComparer.Ordinal);

        // We use a PackerManager per backup run; any sealed packs are written immediately.
        await using var packer = new PackerManager(masterKey, config.PackSize);

        // Helper: write a sealed pack to disk.
        async Task WriteSealedPack(SealedPack sp)
        {
            var packPath = Path.Combine(packsRoot, sp.PackId.Value + ".pack");
            await File.WriteAllBytesAsync(packPath, sp.EncryptedBytes, cancellationToken);
            newEntries.AddRange(sp.IndexEntries);
        }

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info       = new FileInfo(filePath);
            var chunkHashes = new List<BlobHash>();

            // Chunk the file and process each chunk.
            int newChunksThisFile = 0;
            await using var fileStream = File.OpenRead(filePath);
            await foreach (var chunk in chunker.ChunkAsync(fileStream, cancellationToken))
            {
                var chunkBytes = chunk.Data.ToArray();
                var blobHash   = BlobHash.FromBytes(chunkBytes, masterKey);
                chunkHashes.Add(blobHash);

                bool alreadyKnown =
                    existingIndex.ContainsKey(blobHash.Value) ||
                    seenThisRun.Contains(blobHash.Value);

                if (alreadyKnown)
                {
                    // Already stored — skip
                    continue;
                }

                // New chunk — add to packer.
                newChunksThisFile++;
                seenThisRun.Add(blobHash.Value);
                var blob   = new BlobToPack(blobHash, BlobType.Data, chunkBytes);
                var sealed_ = await packer.AddAsync(blob, cancellationToken);
                if (sealed_ is not null)
                    await WriteSealedPack(sealed_);
            }

            // A file is "deduplicated" if it contributed zero new chunks.
            if (newChunksThisFile == 0)
                deduplicated++;
            else
                stored++;

            snapshotFiles.Add(new BackupSnapshotFile(info.FullName, chunkHashes, info.Length));
        }

        // Flush remaining blobs in the packer.
        var flushed = await packer.FlushAsync(cancellationToken);
        if (flushed is not null)
            await WriteSealedPack(flushed);

        // Create the snapshot record.
        var snapshot = new Snapshot(
            SnapshotId.New(),
            DateTimeOffset.UtcNow,
            TreeHash.Empty,
            inputPaths,
            Environment.MachineName,
            Environment.UserName,
            Array.Empty<string>(),
            null);

        // Persist snapshot.
        var snapshotPath = Path.Combine(snapshotRoot, snapshot.Id.Value + ".json");
        await using (var stream = File.Create(snapshotPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new BackupSnapshotDocument(snapshot, snapshotFiles),
                JsonDefaults.Options,
                cancellationToken);
        }

        // Persist index delta for this snapshot.
        if (newEntries.Count > 0)
            await WriteIndexAsync(repoPath, snapshot.Id, newEntries, cancellationToken);

        return (snapshot, stored, deduplicated);
    }

    private static IEnumerable<string> ExpandInputFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    yield return Path.GetFullPath(file);
                }
            }
        }
    }

    // ── List snapshots ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<Snapshot> ListSnapshotsAsync(
        string repoPath,
        string passphrase,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Validate passphrase before listing
        _ = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);

        var snapshotRoot = Path.Combine(repoPath, "snapshots");
        if (!Directory.Exists(snapshotRoot))
            yield break;

        foreach (var snapshotFile in Directory.EnumerateFiles(snapshotRoot, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(snapshotFile);
            var doc = await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
                stream, JsonDefaults.Options, cancellationToken);
            if (doc is not null)
                yield return doc.Snapshot;
        }
    }

    // ── Plan restore ─────────────────────────────────────────────────────────

    public async ValueTask<(IReadOnlyList<BackupSnapshotFile> Files, long TotalBytes)> PlanRestoreAsync(
        string repoPath,
        string passphrase,
        string snapshotId,
        string? includePattern,
        CancellationToken cancellationToken = default)
    {
        // Validate passphrase
        _ = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);

        var snapshotRoot = Path.Combine(repoPath, "snapshots");

        var snapshotFile = Directory
            .EnumerateFiles(snapshotRoot, "*.json")
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Equals(snapshotId, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(snapshotId, StringComparison.OrdinalIgnoreCase);
            });

        if (snapshotFile is null)
            throw new InvalidOperationException($"Snapshot '{snapshotId}' not found.");

        await using var stream = File.OpenRead(snapshotFile);
        var doc = await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
            stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException("Failed to read snapshot.");

        var files = doc.Files;
        if (!string.IsNullOrEmpty(includePattern))
        {
            files = files
                .Where(f => f.Path.Contains(includePattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var totalBytes = files.Sum(f => f.Size);
        return (files, totalBytes);
    }

    // ── Restore file ─────────────────────────────────────────────────────────

    public async ValueTask RestoreFileAsync(
        string repoPath,
        string passphrase,
        BackupSnapshotFile file,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var masterKey = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);
        var index     = await LoadIndexAsync(repoPath, cancellationToken);
        var packsRoot = Path.Combine(repoPath, "packs");

        var relativePath = GetRelativePath(file.Path);
        var outputPath   = Path.Combine(targetPath, relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? targetPath);

        // Cache for pack files we've already read in this restore call.
        var packCache = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

        await using var outputStream = File.Create(outputPath);

        foreach (var chunkHash in file.ChunkHashes)
        {
            if (!index.TryGetValue(chunkHash.Value, out var entry))
                throw new InvalidOperationException(
                    $"Blob not found in index for file: {file.Path} (hash: {chunkHash.Value})");

            var packIdStr = entry.PackId.Value;
            if (!packCache.TryGetValue(packIdStr, out var packBlobs))
            {
                var packPath    = Path.Combine(packsRoot, packIdStr + ".pack");
                var packBytes   = await File.ReadAllBytesAsync(packPath, cancellationToken);
                var (blobs, _)  = await PackReader.ExtractAsync(packBytes, masterKey, cancellationToken);
                packBlobs       = blobs;
                packCache[packIdStr] = packBlobs;
            }

            if (!packBlobs.TryGetValue(chunkHash.Value, out var chunkData))
                throw new InvalidDataException(
                    $"Chunk '{chunkHash.Value}' not found in pack '{packIdStr}'.");

            // Verify HMAC integrity.
            var actualHash = BlobHash.FromBytes(chunkData, masterKey);
            if (actualHash != chunkHash)
                throw new InvalidDataException(
                    $"Integrity check failed for chunk {chunkHash.Value}: got {actualHash.Value}");

            await outputStream.WriteAsync(chunkData, cancellationToken);
        }
    }

    private static string GetRelativePath(string absolutePath)
    {
        if (!Path.IsPathRooted(absolutePath))
            return absolutePath;

        var parts = absolutePath.Replace('\\', '/').Split('/');
        return parts.Length >= 2
            ? string.Join(Path.DirectorySeparatorChar.ToString(), parts[^2..])
            : parts[^1];
    }

    private sealed record BackupSnapshotDocument(Snapshot Snapshot, IReadOnlyList<BackupSnapshotFile> Files);
}
