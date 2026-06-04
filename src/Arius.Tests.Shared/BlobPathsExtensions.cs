using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared;

public static class BlobPathsExtensions
{
    extension(BlobPaths)
    {
        public static RelativePath FileTreePath(string name) => BlobPaths.FileTreesPrefix / PathSegment.Parse(name);
        public static RelativePath ChunkPath(string name)    => BlobPaths.ChunksPrefix / PathSegment.Parse(name);
        public static RelativePath SnapshotPath(DateTimeOffset timestamp) => BlobPaths.SnapshotPath(timestamp.UtcDateTime.ToString(SnapshotService.TimestampFormat));
    }
}
