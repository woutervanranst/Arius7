using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Shared.Compression.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.Compression;

public class ZstdCompatibilityTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RepositoryPayloads_AreReadableAcrossCodecs(bool encrypted, bool previousWriter)
    {
        var encryption = encrypted ? IEncryptionService.EncryptedInstance : IEncryptionService.PlaintextInstance;
        ICompressionService previous = new PreviousZstdCompressionService();
        ICompressionService framework = new ZstdCompressionService();
        var writer = previousWriter ? previous : framework;
        var reader = previousWriter ? framework : previous;

        var content = new byte[256 * 1024];
        new Random(220).NextBytes(content);
        var contentHash = encryption.ComputeHash(content);
        var chunkHash = ChunkHash.Parse(contentHash);
        var blobs = new FakeInMemoryBlobContainerService();
        var upload = new ChunkStorageService(blobs, encryption, writer);
        await upload.UploadLargeAsync(chunkHash, new MemoryStream(content), content.Length, BlobTier.Hot, 0);
        await using (var restored = await new ChunkStorageService(blobs, encryption, reader).DownloadAsync(chunkHash))
        {
            using var output = new MemoryStream();
            await restored.CopyToAsync(output);
            output.ToArray().ShouldBe(content);
            encryption.ComputeHash(output.ToArray()).ShouldBe(contentHash);
        }

        FileTreeEntry[] entries =
        [
            new FileEntry
            {
                Name = PathSegment.Parse("binary.dat"),
                ContentHash = contentHash,
                Created = DateTimeOffset.UnixEpoch,
                Modified = DateTimeOffset.UnixEpoch,
            },
        ];
        var canonicalTree = FileTreeSerializer.Serialize(entries);
        var rootHash = FileTreeHash.Parse(encryption.ComputeHash(canonicalTree));
        using var storedTree = new MemoryStream();
        await using (var encryptedTree = encryption.WrapForEncryption(storedTree))
        await using (var compressedTree = writer.WrapForCompression(encryptedTree))
            await compressedTree.WriteAsync(canonicalTree);
        await using (var decryptedTree = encryption.WrapForDecryption(new MemoryStream(storedTree.ToArray())))
        await using (var decompressedTree = reader.WrapForDecompression(decryptedTree))
        {
            using var output = new MemoryStream();
            await decompressedTree.CopyToAsync(output);
            output.ToArray().ShouldBe(canonicalTree);
            FileTreeHash.Parse(encryption.ComputeHash(output.ToArray())).ShouldBe(rootHash);
            FileTreeSerializer.Deserialize(output.ToArray()).ShouldBe(entries);
        }

        var shard = new Shard();
        var entry = new ShardEntry(contentHash, chunkHash, content.Length, 123, BlobTier.Hot);
        shard.AddOrUpdate(entry);
        var storedShard = await ShardSerializer.SerializeAsync(shard, encryption, writer);
        var restoredShard = ShardSerializer.Deserialize(new MemoryStream(storedShard), encryption, reader);
        restoredShard.Entries.ShouldBe([entry]);

        var snapshot = new SnapshotManifest
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            RootHash = rootHash,
            FileCount = 1,
            OriginalSize = content.Length,
            AriusVersion = "compatibility",
        };
        var storedSnapshot = await SnapshotSerializer.SerializeAsync(snapshot, encryption, writer);
        (await SnapshotSerializer.DeserializeAsync(storedSnapshot, encryption, reader)).ShouldBe(snapshot);
    }

    [Test]
    public async Task ExistingEncryptedZstdGoldenChunk_RemainsReadable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles",
            "b886c2f18e3a1be5bfef76821ca1751126c6534ac267dd539cbbabe09801682f");
        var encryption = IEncryptionService.EncryptedInstance;
        await using var encrypted = File.OpenRead(path);
        await using var decrypted = encryption.WrapForDecryption(encrypted);
        await using var decompressed = new ZstdCompressionService().WrapForDecompression(decrypted);
        using var output = new MemoryStream();
        await decompressed.CopyToAsync(output);

        output.ToArray().ShouldBe("Hello, zstd golden file!"u8.ToArray());
        encryption.ComputeHash(output.ToArray()).ToString().ShouldBe(Path.GetFileName(path));
    }
}
