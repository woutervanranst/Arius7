using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared;

public static class BlobPathStrings
{
    public static string Chunk(ChunkHash hash) => BlobPaths.ChunkPath(hash).ToString();

    public static string ThinChunk(ContentHash hash) => BlobPaths.ThinChunkPath(hash).ToString();

    public static string ChunkRehydrated(ChunkHash hash) => BlobPaths.ChunkRehydratedPath(hash).ToString();

    public static string FileTree(FileTreeHash hash) => BlobPaths.FileTreePath(hash).ToString();

    public static string Snapshot(string name) => BlobPaths.SnapshotPath(name).ToString();

    public static string ChunkIndexShard(string prefix) => BlobPaths.ChunkIndexShardPath(PathSegment.Parse(prefix)).ToString();
}
