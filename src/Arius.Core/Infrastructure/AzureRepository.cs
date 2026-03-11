using System.Security.Cryptography;
using System.Text.Json;
using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

/// <summary>
/// Azure-backed repository implementation.
/// All storage operations go through <see cref="IBlobStorageProvider"/>.
/// No local filesystem is used for repository data.
/// </summary>
public sealed class AzureRepository
{
    private const int RepoVersion = 1;

    // ── Azure blob layout ──────────────────────────────────────────────────────
    // config                       — plain JSON (RepoConfig)
    // keys/{id}                    — plain JSON with encrypted master key payload
    // snapshots/{id}               — plain JSON (BackupSnapshotDocument)
    // index/{id}                   — plain JSON (IndexEntry[])
    // trees/{hash}                 — plain JSON (TreeNode[])
    // data/{prefix2}/{packId}      — encrypted pack file

    private static string SnapshotBlobName(SnapshotId id)    => $"snapshots/{id.Value}";
    private static string IndexBlobName(SnapshotId id)       => $"index/{id.Value}";
    private static string TreeBlobName(TreeHash hash)        => $"trees/{hash.Value}";
    private static string DataBlobName(PackId packId)        => $"data/{packId.Value[..2]}/{packId.Value}";
    private static string KeyBlobName(string keyId)          => $"keys/{keyId}";
    private const  string ConfigBlobName                     = "config";

    private readonly IBlobStorageProvider _storage;

    public AzureRepository(IBlobStorageProvider storage)
    {
        _storage = storage;
    }

    // ── Init ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new repository: writes config + first key file to Azure (Cold tier).
    /// </summary>
    public async ValueTask<(RepoId RepoId, string ConfigBlobName, string KeyBlobName)> InitAsync(
        string passphrase,
        long packSize   = 10 * 1024 * 1024,
        int chunkMin    = 256 * 1024,
        int chunkAvg    = 1024 * 1024,
        int chunkMax    = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var repoConfig = new RepoConfig(
            RepoId.New(),
            RepoVersion,
            RandomNumberGenerator.GetInt32(int.MaxValue),
            packSize,
            chunkMin,
            chunkAvg,
            chunkMax);

        // Upload config (Cold tier, plain JSON)
        var configJson  = JsonSerializer.SerializeToUtf8Bytes(repoConfig, JsonDefaults.Options);
        await _storage.UploadAsync(ConfigBlobName, new MemoryStream(configJson),
            BlobAccessTier.Cold, cancellationToken);

        // Generate master key + write first key file (Cold tier)
        var masterKey      = CryptoService.GenerateMasterKey();
        const string keyId = "default";
        var keyBlobName    = KeyBlobName(keyId);
        await UploadKeyFileAsync(keyId, masterKey, passphrase, cancellationToken);

        return (repoConfig.RepoId, ConfigBlobName, keyBlobName);
    }

    // ── Config ────────────────────────────────────────────────────────────────

    public async Task<RepoConfig> LoadConfigAsync(CancellationToken ct = default)
    {
        await using var stream = await _storage.DownloadAsync(ConfigBlobName, ct);
        return await JsonSerializer.DeserializeAsync<RepoConfig>(stream, JsonDefaults.Options, ct)
            ?? throw new InvalidOperationException("Failed to read repo config.");
    }

    // ── Key management ────────────────────────────────────────────────────────

    /// <summary>
    /// Tries all key files in keys/ with the given passphrase.
    /// Returns the master key on success, or null if no key matches.
    /// </summary>
    public async Task<byte[]?> TryUnlockAsync(string passphrase, CancellationToken ct = default)
    {
        await foreach (var item in _storage.ListAsync("keys/", ct))
        {
            await using var stream = await _storage.DownloadAsync(item.Name, ct);
            KeyFile? keyFile;
            try
            {
                keyFile = await JsonSerializer.DeserializeAsync<KeyFile>(
                    stream, JsonDefaults.Options, ct);
            }
            catch
            {
                continue;
            }
            if (keyFile is null) continue;

            try
            {
                return await CryptoService.DecryptMasterKeyAsync(
                    keyFile.EncryptedMasterKey, passphrase, keyFile.Iterations, ct);
            }
            catch (CryptographicException)
            {
                // Wrong passphrase — try next
            }
        }
        return null;
    }

    /// <summary>Unlocks or throws <see cref="InvalidOperationException"/>.</summary>
    public async Task<byte[]> UnlockAsync(string passphrase, CancellationToken ct = default)
        => await TryUnlockAsync(passphrase, ct)
           ?? throw new InvalidOperationException("Invalid passphrase.");

    /// <summary>Lists the IDs of all key files in keys/.</summary>
    public async IAsyncEnumerable<string> ListKeyBlobsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _storage.ListAsync("keys/", ct))
            yield return item.Name.Replace("keys/", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds a new key file (or overwrites an existing one) with the supplied
    /// <paramref name="masterKey"/> encrypted under <paramref name="passphrase"/>.
    /// </summary>
    public Task AddKeyAsync(string keyId, byte[] masterKey, string passphrase, CancellationToken ct = default)
        => UploadKeyFileAsync(keyId, masterKey, passphrase, ct);

    /// <summary>Deletes the key file for <paramref name="keyId"/>.</summary>
    public Task RemoveKeyAsync(string keyId, CancellationToken ct = default)
        => _storage.DeleteAsync(KeyBlobName(keyId), ct).AsTask();

    private async Task UploadKeyFileAsync(
        string keyId, byte[] masterKey, string passphrase, CancellationToken ct)
    {
        var encryptedMasterKey = await CryptoService.EncryptMasterKeyAsync(
            masterKey, passphrase, CryptoService.DefaultIterations, ct);

        var ciphertextBytes = Convert.FromBase64String(encryptedMasterKey);
        var saltHex         = Convert.ToHexString(ciphertextBytes[8..16]).ToLowerInvariant();

        var keyFile  = new KeyFile(saltHex, CryptoService.DefaultIterations, encryptedMasterKey);
        var keyBytes = JsonSerializer.SerializeToUtf8Bytes(keyFile, JsonDefaults.Options);

        await _storage.UploadAsync(KeyBlobName(keyId), new MemoryStream(keyBytes),
            BlobAccessTier.Cold, ct);
    }

    // ── Index ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and merges all index files from index/ into a single dictionary.
    /// Returns blobHash.Value → IndexEntry.
    /// </summary>
    public async Task<Dictionary<string, IndexEntry>> LoadIndexAsync(CancellationToken ct = default)
    {
        var merged = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        await foreach (var item in _storage.ListAsync("index/", ct))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await _storage.DownloadAsync(item.Name, ct);
            var entries = await JsonSerializer.DeserializeAsync<IndexEntry[]>(
                stream, JsonDefaults.Options, ct);
            if (entries is null) continue;
            foreach (var e in entries)
                merged[e.BlobHash.Value] = e;
        }

        return merged;
    }

    /// <summary>
    /// Writes an index delta for a snapshot to index/{snapshotId}.
    /// </summary>
    public async Task WriteIndexAsync(
        SnapshotId snapshotId,
        IEnumerable<IndexEntry> entries,
        CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries.ToArray(), JsonDefaults.Options);
        await _storage.UploadAsync(IndexBlobName(snapshotId), new MemoryStream(bytes),
            BlobAccessTier.Cold, ct);
    }

    // ── Snapshots ────────────────────────────────────────────────────────────

    /// <summary>Lists all snapshot documents stored in snapshots/.</summary>
    public async IAsyncEnumerable<BackupSnapshotDocument> ListSnapshotDocumentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _storage.ListAsync("snapshots/", ct))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await _storage.DownloadAsync(item.Name, ct);
            var doc = await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
                stream, JsonDefaults.Options, ct);
            if (doc is not null)
                yield return doc;
        }
    }

    /// <summary>Loads a specific snapshot document by ID (prefix match supported).</summary>
    public async Task<BackupSnapshotDocument> LoadSnapshotDocumentAsync(
        string snapshotIdOrPrefix,
        CancellationToken ct = default)
    {
        string? blobName = null;

        await foreach (var item in _storage.ListAsync("snapshots/", ct))
        {
            var name = item.Name.Replace("snapshots/", "", StringComparison.Ordinal);
            if (name.Equals(snapshotIdOrPrefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(snapshotIdOrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                blobName = item.Name;
                break;
            }
        }

        if (blobName is null)
            throw new InvalidOperationException($"Snapshot '{snapshotIdOrPrefix}' not found.");

        await using var stream = await _storage.DownloadAsync(blobName, ct);
        return await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
            stream, JsonDefaults.Options, ct)
            ?? throw new InvalidOperationException("Failed to read snapshot document.");
    }

    /// <summary>Loads a snapshot document directly by its full blob name (used by cache builder).</summary>
    public async Task<BackupSnapshotDocument> LoadSnapshotDocumentByBlobNameAsync(
        string blobName,
        CancellationToken ct = default)
    {
        await using var stream = await _storage.DownloadAsync(blobName, ct);
        return await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
            stream, JsonDefaults.Options, ct)
            ?? throw new InvalidOperationException($"Failed to read snapshot document '{blobName}'.");
    }

    /// <summary>Writes a snapshot document to snapshots/{id} (Cold tier).</summary>
    public async Task WriteSnapshotAsync(
        BackupSnapshotDocument doc,
        CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, JsonDefaults.Options);
        await _storage.UploadAsync(SnapshotBlobName(doc.Snapshot.Id), new MemoryStream(bytes),
            BlobAccessTier.Cold, ct);
    }

    /// <summary>Deletes a snapshot blob from snapshots/{id}.</summary>
    public async Task DeleteSnapshotAsync(SnapshotId id, CancellationToken ct = default)
        => await _storage.DeleteAsync(SnapshotBlobName(id), ct);

    // ── Data packs ────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a sealed pack to data/{prefix2}/{packId} with the specified tier.
    /// The caller decides the tier (default Archive for backup, or --tier override).
    /// </summary>
    public async Task UploadPackAsync(
        SealedPack pack,
        BlobAccessTier tier,
        CancellationToken ct = default)
    {
        var blobName = DataBlobName(pack.PackId);
        await _storage.UploadAsync(blobName, new MemoryStream(pack.EncryptedBytes), tier, ct);
    }

    /// <summary>
    /// Downloads a pack by ID and returns its encrypted bytes.
    /// Caller is responsible for decryption.
    /// </summary>
    public async Task<byte[]> DownloadPackAsync(PackId packId, CancellationToken ct = default)
    {
        var blobName = DataBlobName(packId);
        await using var stream = await _storage.DownloadAsync(blobName, ct);
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Deletes a data pack blob from data/{prefix2}/{packId}.</summary>
    public async Task DeletePackAsync(PackId packId, CancellationToken ct = default)
        => await _storage.DeleteAsync(DataBlobName(packId), ct);

    // ── Cache-support helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Lists blobs under <paramref name="prefix"/> whose name is lexicographically
    /// greater than <paramref name="afterName"/> — used by the cache builder for delta sync.
    /// </summary>
    public async IAsyncEnumerable<BlobItem> ListBlobsAfterAsync(
        string prefix,
        string afterName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _storage.ListAsync(prefix, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(item.Name, afterName) > 0)
                yield return item;
        }
    }

    /// <summary>Loads a single index blob by its full blob name; returns its entries.</summary>
    public async Task<IndexEntry[]> LoadIndexBlobAsync(string blobName, CancellationToken ct = default)
    {
        await using var stream = await _storage.DownloadAsync(blobName, ct);
        return await JsonSerializer.DeserializeAsync<IndexEntry[]>(stream, JsonDefaults.Options, ct)
            ?? [];
    }

    /// <summary>Loads a single tree blob by its full blob name.</summary>
    public async Task<(TreeHash Hash, IReadOnlyList<TreeNode> Nodes)> LoadTreeBlobAsync(
        string blobName,
        CancellationToken ct = default)
    {
        // Blob name format: trees/{hash}
        var hashValue = blobName.Replace("trees/", "", StringComparison.Ordinal);
        await using var stream = await _storage.DownloadAsync(blobName, ct);
        var nodes = await JsonSerializer.DeserializeAsync<List<TreeNode>>(
            stream, JsonDefaults.Options, ct) as IReadOnlyList<TreeNode>
            ?? throw new InvalidOperationException($"Failed to read tree blob '{blobName}'.");
        return (new TreeHash(hashValue), nodes);
    }

    /// <summary>
    /// Writes tree nodes to trees/{hash} (Cold tier).
    /// The hash is computed by the caller from the serialised content.
    /// </summary>
    public async Task WriteTreeAsync(
        TreeHash hash,
        IReadOnlyList<TreeNode> nodes,
        CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(nodes, JsonDefaults.Options);
        await _storage.UploadAsync(TreeBlobName(hash), new MemoryStream(bytes),
            BlobAccessTier.Cold, ct);
    }

    /// <summary>Reads tree nodes from trees/{hash}.</summary>
    public async Task<IReadOnlyList<TreeNode>> ReadTreeAsync(
        TreeHash hash,
        CancellationToken ct = default)
    {
        await using var stream = await _storage.DownloadAsync(TreeBlobName(hash), ct);
        return await JsonSerializer.DeserializeAsync<List<TreeNode>>(
            stream, JsonDefaults.Options, ct) as IReadOnlyList<TreeNode>
            ?? throw new InvalidOperationException($"Failed to read tree '{hash.Value}'.");
    }

    /// <summary>
    /// Computes the <see cref="TreeHash"/> for a set of tree nodes
    /// (SHA-256 of the canonical JSON).
    /// </summary>
    public static TreeHash ComputeTreeHash(IReadOnlyList<TreeNode> nodes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(nodes, JsonDefaults.Options);
        var hash  = SHA256.HashData(bytes);
        return new TreeHash(Convert.ToHexString(hash).ToLowerInvariant());
    }
}

/// <summary>Snapshot document stored as a single blob in Azure.</summary>
public sealed record BackupSnapshotDocument(
    Snapshot Snapshot,
    IReadOnlyList<BackupSnapshotFile> Files);
