using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

internal readonly record struct FastHashResult(ContentHash? Hash, string Reason)
{
    public bool IsHit => Hash is not null;
    public static FastHashResult Hit(ContentHash hash, string reason) => new(hash, reason);
    public static FastHashResult Miss(string reason) => new(null, reason);
}
