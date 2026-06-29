using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

internal interface IHashCacheService
{
    FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now);
    void Record(RelativePath path, long size, FileChangeSignals? signals, byte[] sparseFingerprint, ContentHash hash, long now);
}
