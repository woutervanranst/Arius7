namespace Arius.Core.Shared.HashCache;

[SharedWithinAssembly]
internal interface IHashCacheService
{
    FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now);
    void Record(RelativePath path, long size, FileChangeSignals? signals, long mtimeTicks, byte[] sparseFingerprint, ContentHash hash, long now);
}

internal readonly record struct FastHashResult(ContentHash? Hash, string Reason)
{
    public        bool           IsHit                                => Hash is not null;
    public static FastHashResult Hit(ContentHash hash, string reason) => new(hash, reason);
    public static FastHashResult Miss(string reason)                  => new(null, reason);
}


/// <summary>
/// Per-repository fast-hash facade: the verdict ladder over <see cref="HashCacheLocalStore"/>.
/// Validates against the live file (never a pointer); a miss means the caller must full-hash.
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
        _store.Upsert(new HashCacheEntry(
            Path: path, Size: size,
            MtimeTicks: mtimeTicks, // the file's last-write time; diagnostics only, not in the verdict
            CtimeTicks: signals?.CtimeTicks,
            Inode:      signals?.Inode,
            Dev:        signals?.Dev,
            SignalSet:  signals?.SignalSet ?? SignalSets.None,
            SparseFingerprint: sparseFingerprint,
            FpAlgo:     SparseFingerprint.Algo,
            ContentHash: hash,
            LastVerifiedTicks: now));
    }
}
