namespace Arius.Core.Shared.HashCache;

internal readonly record struct FastHashResult(ContentHash? Hash, string Reason)
{
    public bool IsHit => Hash is not null;
    public static FastHashResult Hit(ContentHash hash, string reason) => new(hash, reason);
    public static FastHashResult Miss(string reason) => new(null, reason);
}

/// <summary>Cheap, platform-provided change signals for one file. See <see cref="SignalSets"/>.</summary>
[SharedWithinAssembly]
internal readonly record struct FileChangeSignals(long CtimeTicks, string Inode, string Dev, int SignalSet);

/// <summary>One persisted hashcache row: the cheap signals + sparse fingerprint + cached content hash for a path.</summary>
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

/// <summary>Provenance tag stored on a hashcache row so signals are only compared within the same source.</summary>
[SharedWithinAssembly]
internal static class SignalSets
{
    public const int None = 0;
    public const int Posix = 1;
    public const int Windows = 2;
}
