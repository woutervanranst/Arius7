using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

/// <summary>
/// Builds and incrementally syncs a <see cref="RepositoryCache"/> from Azure
/// (via <see cref="AzureRepository"/>).
///
/// Task 8.2 — full build: download all index/snapshot/tree blobs → populate SQLite.
/// Task 8.3 — delta sync: download only blobs added since the last watermark.
/// </summary>
public sealed class RepositoryCacheBuilder
{
    private readonly AzureRepository _repo;
    private readonly RepositoryCache _cache;

    public RepositoryCacheBuilder(AzureRepository repo, RepositoryCache cache)
    {
        _repo  = repo;
        _cache = cache;
    }

    /// <summary>
    /// Full rebuild: resets the watermark and reloads everything from Azure.
    /// </summary>
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        // Reset watermark to empty so SyncCoreAsync downloads everything
        _cache.Watermark = "";
        await SyncCoreAsync(watermark: "", ct);
    }

    /// <summary>
    /// Delta sync: downloads only blobs added since the stored watermark.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        var watermark = _cache.Watermark;
        await SyncCoreAsync(watermark, ct);
    }

    private async Task SyncCoreAsync(string watermark, CancellationToken ct)
    {
        string highWater = watermark;

        // ── Index blobs ──────────────────────────────────────────────────────
        await foreach (var item in _repo.ListBlobsAfterAsync("index/", watermark, ct))
        {
            ct.ThrowIfCancellationRequested();

            var entries = await _repo.LoadIndexBlobAsync(item.Name, ct);
            _cache.InTransaction(() =>
            {
                foreach (var e in entries)
                {
                    _cache.UpsertBlob(e);
                    _cache.UpsertPack(e.PackId);
                }
            });

            if (string.CompareOrdinal(item.Name, highWater) > 0)
                highWater = item.Name;
        }

        // ── Snapshot blobs ───────────────────────────────────────────────────
        await foreach (var item in _repo.ListBlobsAfterAsync("snapshots/", watermark, ct))
        {
            ct.ThrowIfCancellationRequested();

            var doc = await _repo.LoadSnapshotDocumentByBlobNameAsync(item.Name, ct);
            _cache.InTransaction(() => _cache.UpsertSnapshot(doc.Snapshot));

            if (string.CompareOrdinal(item.Name, highWater) > 0)
                highWater = item.Name;
        }

        // ── Tree blobs ───────────────────────────────────────────────────────
        await foreach (var item in _repo.ListBlobsAfterAsync("trees/", watermark, ct))
        {
            ct.ThrowIfCancellationRequested();

            var (hash, nodes) = await _repo.LoadTreeBlobAsync(item.Name, ct);
            _cache.InTransaction(() => _cache.UpsertTree(hash, nodes));

            if (string.CompareOrdinal(item.Name, highWater) > 0)
                highWater = item.Name;
        }

        // Persist the new watermark
        if (string.CompareOrdinal(highWater, watermark) > 0)
            _cache.Watermark = highWater;
    }
}
