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
        var contentHash = FakeContentHash('a');

        var hashed = new HashedFilePair(pair, contentHash, @"C:\repo");

        hashed.ContentHash.ShouldBe(contentHash);
    }

    [Test]
    public void IndexEntry_StoresTypedHashes()
    {
        var contentHash = FakeContentHash('a');
        var chunkHash = FakeChunkHash('b');

        var entry = new IndexEntry(
            contentHash,
            chunkHash,
            10,
            8);

        entry.ContentHash.ShouldBe(contentHash);
        entry.ChunkHash.ShouldBe(chunkHash);
    }

    [Test]
    public void TarEntry_StoresTypedContentHash()
    {
        var pair = CreateFilePair();
        var contentHash = FakeContentHash('a');
        var hashed = new HashedFilePair(pair, contentHash, @"C:\repo");
        var entry = new TarEntry(contentHash, 12, hashed);

        entry.ContentHash.ShouldBe(contentHash);
    }

    [Test]
    public void SealedTar_StoresTypedChunkHash()
    {
        var chunkHash = FakeChunkHash('c');
        var sealedTar = new SealedTar(@"C:\temp\bundle.tar", chunkHash, 12, []);

        sealedTar.TarHash.ShouldBe(chunkHash);
    }

    [Test]
    public void ArchiveEvents_StoreTypedHashes()
    {
        var contentHash = FakeContentHash('a');
        var chunkHash = FakeChunkHash('b');
        var rootHash = FakeFileTreeHash('c');

        new FileHashedEvent("file.txt", contentHash).ContentHash.ShouldBe(contentHash);
        new ChunkUploadingEvent(chunkHash, 12).ChunkHash.ShouldBe(chunkHash);
        new ChunkUploadedEvent(chunkHash, 10).ChunkHash.ShouldBe(chunkHash);
        new TarBundleSealingEvent(1, 12, chunkHash, [contentHash]).TarHash.ShouldBe(chunkHash);
        new TarBundleUploadedEvent(chunkHash, 10, 1).TarHash.ShouldBe(chunkHash);
        new TarEntryAddedEvent(contentHash, 1, 12).ContentHash.ShouldBe(contentHash);
        new SnapshotCreatedEvent(rootHash, DateTimeOffset.UtcNow, 1).RootHash.ShouldBe(rootHash);
    }
}
