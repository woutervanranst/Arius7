using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.LocalFile;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class ArchiveModelTypingTests
{
    private static FilePair CreateFilePair() => new()
    {
        RelativePath = "file.txt",
        BinaryExists = true,
        PointerExists = false,
        PointerHash = null,
        FileSize = 12,
        Created = null,
        Modified = null
    };

    [Test]
    public void HashedFilePair_StoresTypedContentHash()
    {
        var pair = CreateFilePair();
        var contentHash = ContentHash.Parse(new string('a', 64));

        var hashed = new HashedFilePair(pair, contentHash, @"C:\repo");

        hashed.ContentHash.ShouldBe(contentHash);
    }

    [Test]
    public void IndexEntry_StoresTypedHashes()
    {
        var entry = new IndexEntry(
            ContentHash.Parse(new string('a', 64)),
            ChunkHash.Parse(new string('b', 64)),
            10,
            8);

        entry.ContentHash.ShouldBe(ContentHash.Parse(new string('a', 64)));
        entry.ChunkHash.ShouldBe(ChunkHash.Parse(new string('b', 64)));
    }

    [Test]
    public void TarEntry_StoresTypedContentHash()
    {
        var pair = CreateFilePair();
        var hashed = new HashedFilePair(pair, ContentHash.Parse(new string('a', 64)), @"C:\repo");
        var entry = new TarEntry(ContentHash.Parse(new string('a', 64)), 12, hashed);

        entry.ContentHash.ShouldBe(ContentHash.Parse(new string('a', 64)));
    }

    [Test]
    public void SealedTar_StoresTypedChunkHash()
    {
        var sealedTar = new SealedTar(@"C:\temp\bundle.tar", ChunkHash.Parse(new string('c', 64)), 12, []);

        sealedTar.TarHash.ShouldBe(ChunkHash.Parse(new string('c', 64)));
    }

    [Test]
    public void ArchiveEvents_StoreTypedHashes()
    {
        var contentHash = ContentHash.Parse(new string('a', 64));
        var chunkHash = ChunkHash.Parse(new string('b', 64));
        var rootHash = FileTreeHash.Parse(new string('c', 64));

        new FileHashedEvent("file.txt", contentHash).ContentHash.ShouldBe(contentHash);
        new ChunkUploadingEvent(chunkHash, 12).ChunkHash.ShouldBe(chunkHash);
        new ChunkUploadedEvent(chunkHash, 10).ChunkHash.ShouldBe(chunkHash);
        new TarBundleSealingEvent(1, 12, chunkHash, [contentHash]).TarHash.ShouldBe(chunkHash);
        new TarBundleUploadedEvent(chunkHash, 10, 1).TarHash.ShouldBe(chunkHash);
        new TarEntryAddedEvent(contentHash, 1, 12).ContentHash.ShouldBe(contentHash);
        new SnapshotCreatedEvent(rootHash, DateTimeOffset.UtcNow, 1).RootHash.ShouldBe(rootHash);
    }
}
