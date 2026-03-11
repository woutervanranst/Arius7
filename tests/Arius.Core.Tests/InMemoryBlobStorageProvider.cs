using Arius.Core.Application.Abstractions;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// In-memory mock IBlobStorageProvider — no Azure SDK, no Azurite
// Shared across AzureRepositoryTests and RepositoryCacheTests
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class InMemoryBlobStorageProvider : IBlobStorageProvider
{
    private readonly Dictionary<string, (byte[] Data, BlobAccessTier Tier)> _store = new(StringComparer.Ordinal);

    public ValueTask UploadAsync(string blobName, Stream data, BlobAccessTier tier, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        _store[blobName] = (ms.ToArray(), tier);
        return ValueTask.CompletedTask;
    }

    public ValueTask<Stream> DownloadAsync(string blobName, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(blobName, out var entry))
            throw new InvalidOperationException($"Blob not found: {blobName}");
        return ValueTask.FromResult<Stream>(new MemoryStream(entry.Data));
    }

    public ValueTask<bool> ExistsAsync(string blobName, CancellationToken ct = default)
        => ValueTask.FromResult(_store.ContainsKey(blobName));

    public ValueTask<BlobAccessTier?> GetTierAsync(string blobName, CancellationToken ct = default)
        => _store.TryGetValue(blobName, out var e)
            ? ValueTask.FromResult<BlobAccessTier?>(e.Tier)
            : ValueTask.FromResult<BlobAccessTier?>(null);

    public ValueTask<string?> GetArchiveStatusAsync(string blobName, CancellationToken ct = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask SetTierAsync(string blobName, BlobAccessTier tier, CancellationToken ct = default)
    {
        if (_store.TryGetValue(blobName, out var entry))
            _store[blobName] = (entry.Data, tier);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<BlobItem> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (name, (data, _)) in _store.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            yield return new BlobItem(name, data.Length, DateTimeOffset.UtcNow);
        }
        await Task.CompletedTask; // satisfy async requirement
    }

    public ValueTask DeleteAsync(string blobName, CancellationToken ct = default)
    {
        _store.Remove(blobName);
        return ValueTask.CompletedTask;
    }

    // Lease operations — no-op for unit tests
    public ValueTask<string> AcquireLeaseAsync(string blobName, CancellationToken ct = default)
        => ValueTask.FromResult(Guid.NewGuid().ToString());
    public ValueTask RenewLeaseAsync(string blobName, string leaseId, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    public ValueTask ReleaseLeaseAsync(string blobName, string leaseId, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    // Helpers for test assertions
    public bool Contains(string blobName) => _store.ContainsKey(blobName);
    public BlobAccessTier TierOf(string blobName) => _store[blobName].Tier;
    public int Count => _store.Count;
    public IEnumerable<string> Keys => _store.Keys;
}
