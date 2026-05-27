using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared;

public static class BlobPathsExtensions
{
    extension(BlobPaths)
    {
        public static RelativePath FileTreePath(string hash) => BlobPaths.FileTreePath(FileTreeHash.Parse(hash));
        public static RelativePath ChunkPath(string hash)    => BlobPaths.ChunkPath(ChunkHash.Parse(hash));
    }
}