namespace Arius.Core.Shared.HashCache;

/// <summary>One persisted hashcache row: the cheap change signals + sparse fingerprint + cached content hash for a path.</summary>
internal readonly record struct HashCacheEntry(
    RelativePath Path,
    long Size,
    long MtimeTicks,
    long? CtimeTicks,
    string? Inode,
    string? Dev,
    int SignalSet,
    byte[] SparseFingerprint,
    int FpAlgo,
    ContentHash ContentHash,
    long LastVerifiedTicks);