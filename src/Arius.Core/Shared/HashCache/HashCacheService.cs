namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Per-repository fast-hash contract consumed by <c>ArchiveCommandHandler</c>: validates a candidate
/// file against the live filesystem (never a pointer) and reports a hit only when content is provably
/// unchanged. A miss means the caller must full-hash.
/// </summary>
[SharedWithinAssembly]
internal interface IHashCacheService
{
    /// <summary>Attempts to reuse a previously recorded content hash for <paramref name="path"/> without re-hashing it.</summary>
    FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now);

    /// <summary>Persists the content hash and change signals just captured for <paramref name="path"/>, so a later <see cref="TryReuse"/> call can validate against them.</summary>
    void Record(RelativePath path, long size, FileChangeSignals? signals, long mtimeTicks, byte[] sparseFingerprint, ContentHash hash, long now);
}

/// <summary>Verdict from <see cref="IHashCacheService.TryReuse"/>: the reused hash on a hit, plus a diagnostic <see cref="Reason"/> for either outcome.</summary>
internal readonly record struct FastHashResult(ContentHash? Hash, string Reason)
{
    public        bool           IsHit                                => Hash is not null;
    public static FastHashResult Hit(ContentHash hash, string reason) => new(hash, reason);
    public static FastHashResult Miss(string reason)                  => new(null, reason);
}


/// <summary>
/// <see cref="IHashCacheService"/> implementation: the verdict ladder over <see cref="HashCacheLocalStore"/>.
/// <para>
/// The sparse fingerprint has two implementations of the same bytes, one per side:
/// <code>
///  Path   | method                                | how it reads                                                                        | when
///  -------+---------------------------------------+--------------------------------------------------------------------------------------+---------------------------------------------
///  Write  | SparseSamplingStream.Fingerprint()     | siphoned off the full sequential read that happens anyway for the content hash—free  | computed by the caller after a full hash, then handed to .Record()
///  Read   | SparseFingerprint.ComputeBySeeking()   | seeks directly to the sample offsets, never reads the whole file (-> cheap)          | called directly by .TryReuse() to validate a cached row
/// </code>
/// Load-bearing invariant: both must produce identical bytes for identical content under the same
/// <see cref="SparseFingerprint.Algo"/> — otherwise an unchanged file never matches and fast-hash
/// degenerates to always-miss. The FpAlgo guard in <see cref="TryReuse"/> enforces this: bump the
/// algorithm and old rows are discarded rather than compared across incompatible fingerprint definitions.
/// </para>
/// </summary>
[SharedWithinAssembly]
internal sealed class HashCacheService : IHashCacheService
{
    private readonly HashCacheLocalStore _store;

    public HashCacheService(HashCacheLocalStore store) => _store = store;

    public FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now)
    {
        var row = _store.Find(path);
        if (row is null)
            return FastHashResult.Miss("cache miss");
        var e = row.Value;

        if (e.FpAlgo != SparseFingerprint.Algo)
            return FastHashResult.Miss("fp_algo bump");

        if (liveSize != e.Size)
            return FastHashResult.Miss($"size {e.Size}->{liveSize}");

        // ctime fast-lane: same file (dev+inode) and untouched (ctime) → reuse with no reads.
        var sig = fs.TryGetChangeSignals(path);
        if (sig is { } s
            && s.SignalSet == e.SignalSet
            && e.Inode is not null && e.Dev is not null && e.CtimeTicks is not null
            && s.Inode == e.Inode && s.Dev == e.Dev && s.CtimeTicks == e.CtimeTicks)
        {
            _store.Touch(path, now); // one-column UPDATE: nothing else changed on a ctime hit
            return FastHashResult.Hit(e.ContentHash, "ctime match");
        }

        // Floor: sample bytes and compare the fingerprint.
        var liveFp = SparseFingerprint.ComputeBySeeking(fs, path, liveSize);
        if (liveFp.AsSpan().SequenceEqual(e.SparseFingerprint))
        {
            // Update existing value
            _store.Upsert(e with
            {
                CtimeTicks        = sig?.CtimeTicks,
                Inode             = sig?.Inode,
                Dev               = sig?.Dev,
                SignalSet         = sig?.SignalSet ?? SignalSets.None,
                LastVerifiedTicks = now,
            });
            return FastHashResult.Hit(e.ContentHash, "size+fp match");
        }

        return FastHashResult.Miss("fp differs");
    }

    public void Record(RelativePath path, long size, FileChangeSignals? signals, long mtimeTicks, byte[] sparseFingerprint, ContentHash hash, long now)
    {
        // Insert new value
        _store.Upsert(new HashCacheEntry(
            Path:              path, 
            Size:              size,
            MtimeTicks:        mtimeTicks, // the file's last-write time
            CtimeTicks:        signals?.CtimeTicks,
            Inode:             signals?.Inode,
            Dev:               signals?.Dev,
            SignalSet:         signals?.SignalSet ?? SignalSets.None,
            SparseFingerprint: sparseFingerprint,
            FpAlgo:            SparseFingerprint.Algo,
            ContentHash:       hash,
            LastVerifiedTicks: now));
    }
}
