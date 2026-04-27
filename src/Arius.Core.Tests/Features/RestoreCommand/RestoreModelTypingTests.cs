using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreModelTypingTests
{
    [Test]
    public void FileToRestore_StoresTypedContentHash()
    {
        var contentHash = ContentHash.Parse(new string('a', 64));
        var file = new FileToRestore("file.txt", contentHash, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        file.ContentHash.ShouldBe(contentHash);
    }

    [Test]
    public void RestoreEvents_StoreTypedHashes()
    {
        var rootHash = FileTreeHash.Parse(new string('b', 64));
        var chunkHash = ChunkHash.Parse(new string('c', 64));

        new SnapshotResolvedEvent(DateTimeOffset.UtcNow, rootHash, 1).RootHash.ShouldBe(rootHash);
        new ChunkDownloadStartedEvent(chunkHash, "large", 1, 10, 12).ChunkHash.ShouldBe(chunkHash);
        new ChunkDownloadCompletedEvent(chunkHash, 1, 10).ChunkHash.ShouldBe(chunkHash);
    }
}
