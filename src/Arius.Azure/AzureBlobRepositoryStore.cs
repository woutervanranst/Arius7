using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Arius.Core.Application.Backup;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Azure;

/// <summary>
/// Azure Blob Storage–backed repository store.
/// Mirrors the public surface of <c>FileSystemRepositoryStore</c> but uses
/// <see cref="IBlobStorageProvider"/> for all I/O.
/// </summary>
public sealed class AzureBlobRepositoryStore
{
    private const int RepoVersion = 1;

    // ── Blob path helpers ─────────────────────────────────────────────────────

    private static string ConfigBlob()               => "config.json";
    private static string KeyBlob(string keyId)      => $"keys/{keyId}.json";
    private static string SnapshotBlob(string id)    => $"snapshots/{id}.json";
    private static string IndexBlob(string id)       => $"index/{id}.json";
    private static string PackBlob(PackId packId)
    {
        var prefix2 = packId.Value[..2];
        return $"data/{prefix2}/{packId.Value}.pack";
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public async ValueTask<RepoId> InitAsync(
        AzureBlobStorageProvider provider,
        string passphrase,
        long packSize   = PackerManager.DefaultPackSize,
        int chunkMin    = GearChunker.DefaultMin,
        int chunkAvg    = GearChunker.DefaultAvg,
        int chunkMax    = GearChunker.DefaultMax,
        CancellationToken ct = default)
    {
        // Create the container first.
        await provider.CreateContainerIfNotExistsAsync(ct);

        var repoConfig = new RepoConfig(
            RepoId.New(),
            RepoVersion,
            RandomNumberGenerator.GetInt32(int.MaxValue),
            packSize,
            chunkMin,
            chunkAvg,
            chunkMax);

        // Upload config.json at Cold tier.
        await UploadJsonAsync(provider, ConfigBlob(), repoConfig, BlobTier.Cold, ct);

        // Generate master key and write keys/default.json.
        var masterKey           = CryptoService.GenerateMasterKey();
        var encryptedMasterKey  = await CryptoService.EncryptMasterKeyAsync(
            masterKey, passphrase, CryptoService.DefaultIterations, ct);

        // Extract salt from the ciphertext header (skip "Salted__" magic = 8 bytes, take 8 bytes of salt).
        var ciphertextBytes = Convert.FromBase64String(encryptedMasterKey);
        var saltHex         = Convert.ToHexString(ciphertextBytes[8..16]).ToLowerInvariant();

        var keyFile = new KeyFile(saltHex, CryptoService.DefaultIterations, encryptedMasterKey);
        await UploadJsonAsync(provider, KeyBlob("default"), keyFile, BlobTier.Cold, ct);

        return repoConfig.RepoId;
    }

    // ── Master key resolution ─────────────────────────────────────────────────

    private static async Task<byte[]> LoadMasterKeyAsync(
        IBlobStorageProvider provider,
        string passphrase,
        CancellationToken ct)
    {
        await foreach (var blobName in provider.ListAsync("keys/", ct))
        {
            var keyFile = await DownloadJsonAsync<KeyFile>(provider, blobName, ct);
            if (keyFile is null) continue;
            try
            {
                return await CryptoService.DecryptMasterKeyAsync(
                    keyFile.EncryptedMasterKey, passphrase, keyFile.Iterations, ct);
            }
            catch (CryptographicException)
            {
                // Wrong passphrase — try next key file.
            }
        }

        throw new InvalidOperationException("Invalid passphrase.");
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private static async Task<RepoConfig> LoadConfigAsync(
        IBlobStorageProvider provider,
        CancellationToken ct)
    {
        return await DownloadJsonAsync<RepoConfig>(provider, ConfigBlob(), ct)
            ?? throw new InvalidOperationException("Failed to read repo config.");
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, IndexEntry>> LoadIndexAsync(
        IBlobStorageProvider provider,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        await foreach (var blobName in provider.ListAsync("index/", ct))
        {
            ct.ThrowIfCancellationRequested();
            var entries = await DownloadJsonAsync<IndexEntry[]>(provider, blobName, ct);
            if (entries is null) continue;
            foreach (var e in entries)
                merged[e.BlobHash.Value] = e;
        }

        return merged;
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    public async ValueTask<(Snapshot Snapshot, int StoredFiles, int DeduplicatedFiles)> BackupAsync(
        IBlobStorageProvider provider,
        string passphrase,
        IReadOnlyList<string> inputPaths,
        BlobTier targetTier = BlobTier.Archive,
        CancellationToken ct = default)
    {
        var masterKey = await LoadMasterKeyAsync(provider, passphrase, ct);
        var config    = await LoadConfigAsync(provider, ct);
        var chunker   = GearChunker.FromConfig(config);

        var existingIndex = await LoadIndexAsync(provider, ct);

        var files         = ExpandInputFiles(inputPaths).ToList();
        var snapshotFiles = new List<BackupSnapshotFile>(files.Count);
        var newEntries    = new List<IndexEntry>();

        var stored       = 0;
        var deduplicated = 0;
        var seenThisRun  = new HashSet<string>(StringComparer.Ordinal);

        await using var packer = new PackerManager(masterKey, config.PackSize);

        async Task WriteSealedPack(SealedPack sp)
        {
            var packBlobName = PackBlob(sp.PackId);
            using var ms = new MemoryStream(sp.EncryptedBytes);
            await provider.UploadAsync(packBlobName, ms, targetTier, ct);
            newEntries.AddRange(sp.IndexEntries);
        }

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var info       = new FileInfo(filePath);
            var chunkHashes = new List<BlobHash>();
            int newChunksThisFile = 0;

            await using var fileStream = File.OpenRead(filePath);
            await foreach (var chunk in chunker.ChunkAsync(fileStream, ct))
            {
                var chunkBytes = chunk.Data.ToArray();
                var blobHash   = BlobHash.FromBytes(chunkBytes, masterKey);
                chunkHashes.Add(blobHash);

                bool alreadyKnown =
                    existingIndex.ContainsKey(blobHash.Value) ||
                    seenThisRun.Contains(blobHash.Value);

                if (alreadyKnown) continue;

                newChunksThisFile++;
                seenThisRun.Add(blobHash.Value);
                var blob    = new BlobToPack(blobHash, BlobType.Data, chunkBytes);
                var sealed_ = await packer.AddAsync(blob, ct);
                if (sealed_ is not null)
                    await WriteSealedPack(sealed_);
            }

            if (newChunksThisFile == 0)
                deduplicated++;
            else
                stored++;

            snapshotFiles.Add(new BackupSnapshotFile(info.FullName, chunkHashes, info.Length));
        }

        var flushed = await packer.FlushAsync(ct);
        if (flushed is not null)
            await WriteSealedPack(flushed);

        var snapshot = new Snapshot(
            SnapshotId.New(),
            DateTimeOffset.UtcNow,
            TreeHash.Empty,
            inputPaths,
            Environment.MachineName,
            Environment.UserName,
            Array.Empty<string>(),
            null);

        // Upload snapshot at Cold tier.
        var snapshotDoc = new BackupSnapshotDocument(snapshot, snapshotFiles);
        await UploadJsonAsync(provider, SnapshotBlob(snapshot.Id.Value), snapshotDoc, BlobTier.Cold, ct);

        // Upload index delta at Cold tier (only when new entries exist).
        if (newEntries.Count > 0)
            await UploadJsonAsync(provider, IndexBlob(snapshot.Id.Value), newEntries.ToArray(), BlobTier.Cold, ct);

        return (snapshot, stored, deduplicated);
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    public async ValueTask RestoreFileAsync(
        IBlobStorageProvider provider,
        string passphrase,
        BackupSnapshotFile file,
        string targetPath,
        CancellationToken ct = default)
    {
        var masterKey = await LoadMasterKeyAsync(provider, passphrase, ct);
        var index     = await LoadIndexAsync(provider, ct);

        var relativePath = GetRelativePath(file.Path);
        var outputPath   = Path.Combine(targetPath, relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? targetPath);

        // Cache: packId → blob dict (avoid re-downloading the same pack).
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
                var packBlobName = PackBlob(entry.PackId);
                Stream packStream;
                try
                {
                    packStream = await provider.DownloadAsync(packBlobName, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
                {
                    throw new InvalidOperationException(
                        $"Pack '{packIdStr}' is in Archive tier and has not been rehydrated. " +
                        "Use TargetTier=Cold/Hot/Cool for backup, or rehydrate the pack before restoring.", ex);
                }

                byte[] packBytes;
                await using (packStream)
                {
                    using var ms = new MemoryStream();
                    await packStream.CopyToAsync(ms, ct);
                    packBytes = ms.ToArray();
                }

                var (blobs, _) = await PackReader.ExtractAsync(packBytes, masterKey, ct);
                packBlobs = blobs;
                packCache[packIdStr] = packBlobs;
            }

            if (!packBlobs.TryGetValue(chunkHash.Value, out var chunkData))
                throw new InvalidDataException(
                    $"Chunk '{chunkHash.Value}' not found in pack '{packIdStr}'.");

            var actualHash = BlobHash.FromBytes(chunkData, masterKey);
            if (actualHash != chunkHash)
                throw new InvalidDataException(
                    $"Integrity check failed for chunk {chunkHash.Value}: got {actualHash.Value}");

            await outputStream.WriteAsync(chunkData, ct);
        }
    }

    // ── List snapshots ────────────────────────────────────────────────────────

    public async IAsyncEnumerable<Snapshot> ListSnapshotsAsync(
        IBlobStorageProvider provider,
        string passphrase,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = await LoadMasterKeyAsync(provider, passphrase, ct);

        await foreach (var blobName in provider.ListAsync("snapshots/", ct))
        {
            ct.ThrowIfCancellationRequested();
            var doc = await DownloadJsonAsync<BackupSnapshotDocument>(provider, blobName, ct);
            if (doc is not null)
                yield return doc.Snapshot;
        }
    }

    // ── Plan restore ──────────────────────────────────────────────────────────

    public async ValueTask<(IReadOnlyList<BackupSnapshotFile> Files, long TotalBytes)> PlanRestoreAsync(
        IBlobStorageProvider provider,
        string passphrase,
        string snapshotId,
        string? includePattern,
        CancellationToken ct = default)
    {
        _ = await LoadMasterKeyAsync(provider, passphrase, ct);

        BackupSnapshotDocument? doc = null;
        await foreach (var blobName in provider.ListAsync("snapshots/", ct))
        {
            var name = Path.GetFileNameWithoutExtension(blobName);
            if (name.Equals(snapshotId, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(snapshotId, StringComparison.OrdinalIgnoreCase))
            {
                doc = await DownloadJsonAsync<BackupSnapshotDocument>(provider, blobName, ct);
                break;
            }
        }

        if (doc is null)
            throw new InvalidOperationException($"Snapshot '{snapshotId}' not found.");

        var files = doc.Files;
        if (!string.IsNullOrEmpty(includePattern))
        {
            files = files
                .Where(f => f.Path.Contains(includePattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return (files, files.Sum(f => f.Size));
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static async Task UploadJsonAsync<T>(
        IBlobStorageProvider provider,
        string blobName,
        T value,
        BlobTier tier,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, value, JsonDefaults.Options, ct);
        ms.Position = 0;
        await provider.UploadAsync(blobName, ms, tier, ct);
    }

    private static async Task<T?> DownloadJsonAsync<T>(
        IBlobStorageProvider provider,
        string blobName,
        CancellationToken ct)
    {
        await using var stream = await provider.DownloadAsync(blobName, ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, ct);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

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
                    yield return Path.GetFullPath(file);
            }
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

    // ── Internal document types ───────────────────────────────────────────────

    private sealed record BackupSnapshotDocument(Snapshot Snapshot, IReadOnlyList<BackupSnapshotFile> Files);
}
