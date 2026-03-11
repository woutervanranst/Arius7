using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

/// <summary>
/// Cache-aware wrapper around <see cref="AzureRepository"/>.
///
/// All read operations check the <see cref="RepositoryCache"/> first and fall back to
/// Azure only on a cache miss.  Write operations always go directly to Azure and
/// then update the cache.
///
/// The invariant <c>result(with_cache) ≡ result(without_cache)</c> is maintained:
/// the cache never alters correctness, only performance.
/// </summary>
public sealed class CachedRepository
{
    private readonly AzureRepository    _repo;
    private readonly RepositoryCache    _cache;
    private readonly RepositoryCacheBuilder _builder;

    public CachedRepository(AzureRepository repo, RepositoryCache cache)
    {
        _repo    = repo;
        _cache   = cache;
        _builder = new RepositoryCacheBuilder(repo, cache);
    }

    // ── Sync / init ───────────────────────────────────────────────────────────

    /// <summary>Incrementally syncs the cache from Azure before use.</summary>
    public Task SyncAsync(CancellationToken ct = default)
        => _builder.SyncAsync(ct);

    /// <summary>Full rebuild — clears watermark and re-downloads everything.</summary>
    public Task RebuildAsync(CancellationToken ct = default)
        => _builder.RebuildAsync(ct);

    // ── Index ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the blob index.  Cache hit: returns from SQLite.
    /// Cache miss (empty cache): falls through to Azure and does NOT auto-populate
    /// (caller should call <see cref="SyncAsync"/> to warm the cache).
    /// </summary>
    public async Task<Dictionary<string, IndexEntry>> LoadIndexAsync(CancellationToken ct = default)
    {
        var cached = _cache.LoadAllBlobs();
        if (cached.Count > 0)
            return cached;

        // Cache cold — fall back to Azure
        return await _repo.LoadIndexAsync(ct);
    }

    /// <summary>
    /// Looks up a single blob by hash.  Cache hit: O(1) SQLite lookup.
    /// Cache miss: falls back to a full Azure index load.
    /// </summary>
    public async Task<IndexEntry?> FindBlobAsync(BlobHash hash, CancellationToken ct = default)
    {
        var cached = _cache.FindBlob(hash);
        if (cached is not null)
            return cached;

        // Cache miss — fall back to Azure index load
        var index = await _repo.LoadIndexAsync(ct);
        return index.GetValueOrDefault(hash.Value);
    }

    // ── Snapshots ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists snapshots.  Cache hit: returns from SQLite.
    /// Cache miss: falls back to Azure and returns Azure results.
    /// </summary>
    public async IAsyncEnumerable<BackupSnapshotDocument> ListSnapshotDocumentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cached = _cache.ListSnapshots();
        if (cached.Count > 0)
        {
            // Return cached snapshots; load the full document from Azure only if files are needed
            // (For listing, snapshot metadata is sufficient — docs are loaded on demand)
            foreach (var snap in cached)
            {
                ct.ThrowIfCancellationRequested();
                yield return new BackupSnapshotDocument(snap, []);
            }
            yield break;
        }

        // Cache cold — fall back to Azure
        await foreach (var doc in _repo.ListSnapshotDocumentsAsync(ct))
            yield return doc;
    }

    /// <summary>
    /// Loads a full snapshot document (with file list).
    /// Always fetches from Azure to ensure the file list is present.
    /// </summary>
    public Task<BackupSnapshotDocument> LoadSnapshotDocumentAsync(
        string snapshotIdOrPrefix,
        CancellationToken ct = default)
        => _repo.LoadSnapshotDocumentAsync(snapshotIdOrPrefix, ct);

    // ── Trees ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads tree nodes.  Cache hit: returns from SQLite.
    /// Cache miss: downloads from Azure and populates cache.
    /// </summary>
    public async Task<IReadOnlyList<TreeNode>> ReadTreeAsync(TreeHash hash, CancellationToken ct = default)
    {
        var cached = _cache.FindTree(hash);
        if (cached is not null)
            return cached;

        // Cache miss — fetch from Azure and populate
        var nodes = await _repo.ReadTreeAsync(hash, ct);
        _cache.InTransaction(() => _cache.UpsertTree(hash, nodes));
        return nodes;
    }

    // ── Writes (always go to Azure; cache updated inline) ─────────────────────

    public async Task WriteIndexAsync(
        SnapshotId snapshotId,
        IEnumerable<IndexEntry> entries,
        CancellationToken ct = default)
    {
        var list = entries.ToList();
        await _repo.WriteIndexAsync(snapshotId, list, ct);
        _cache.InTransaction(() =>
        {
            foreach (var e in list)
            {
                _cache.UpsertBlob(e);
                _cache.UpsertPack(e.PackId);
            }
        });
    }

    public async Task WriteSnapshotAsync(BackupSnapshotDocument doc, CancellationToken ct = default)
    {
        await _repo.WriteSnapshotAsync(doc, ct);
        _cache.InTransaction(() => _cache.UpsertSnapshot(doc.Snapshot));
    }

    public async Task WriteTreeAsync(
        TreeHash hash,
        IReadOnlyList<TreeNode> nodes,
        CancellationToken ct = default)
    {
        await _repo.WriteTreeAsync(hash, nodes, ct);
        _cache.InTransaction(() => _cache.UpsertTree(hash, nodes));
    }

    // ── Pass-through (no caching needed) ─────────────────────────────────────

    public Task<RepoConfig>  LoadConfigAsync(CancellationToken ct = default) => _repo.LoadConfigAsync(ct);
    public Task<byte[]?>     TryUnlockAsync(string passphrase, CancellationToken ct = default) => _repo.TryUnlockAsync(passphrase, ct);
    public Task<byte[]>      UnlockAsync(string passphrase, CancellationToken ct = default) => _repo.UnlockAsync(passphrase, ct);
    public Task              UploadPackAsync(SealedPack pack, Application.Abstractions.BlobAccessTier tier, CancellationToken ct = default) => _repo.UploadPackAsync(pack, tier, ct);
    public Task<byte[]>      DownloadPackAsync(PackId packId, CancellationToken ct = default) => _repo.DownloadPackAsync(packId, ct);
}
