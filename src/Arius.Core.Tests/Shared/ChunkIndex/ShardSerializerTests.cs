using Arius.Core.Shared.ChunkIndex;
using Arius.Tests.Shared.Compression;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_WithPassphrase_RoundTrips()
    {
        var svc   = IEncryptionService.EncryptedInstance;
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('a'),
                FakeChunkHash('b'),
                512,
                256,
                BlobTier.Archive)
        );

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc, TestCompression.Instance);
        var loaded = ShardSerializer.Deserialize(bytes, svc, TestCompression.Instance);

        loaded.TryLookup(FakeContentHash('a'), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
        e.StorageTierHint.ShouldBe(BlobTier.Archive);
    }

    [Test]
    public async Task Serialize_ThenDeserializeStream_WithPassphrase_RoundTrips()
    {
        var svc   = IEncryptionService.EncryptedInstance;
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('e'),
                FakeChunkHash('f'),
                512,
                256,
                BlobTier.Archive)
        );

        var bytes = await ShardSerializer.SerializeAsync(shard, svc, TestCompression.Instance);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, svc, TestCompression.Instance);

        loaded.TryLookup(FakeContentHash('e'), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('c'),
                FakeChunkHash('d'),
                100,
                40,
                BlobTier.Cool)
        );

        var bytes  = await ShardSerializer.SerializeAsync(shard, IEncryptionService.PlaintextInstance, TestCompression.Instance);
        var loaded = ShardSerializer.Deserialize(bytes, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        loaded.TryLookup(FakeContentHash('c'), out var e).ShouldBeTrue();
        e!.ChunkSize.ShouldBe(40);
        e.StorageTierHint.ShouldBe(BlobTier.Cool);
    }

    [Test]
    public async Task Serialize_ThenDeserializeStream_Plaintext_RoundTrips()
    {
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('8'),
                FakeChunkHash('9'),
                100,
                40,
                BlobTier.Cool)
        );

        var bytes = await ShardSerializer.SerializeAsync(shard, IEncryptionService.PlaintextInstance, TestCompression.Instance);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        loaded.TryLookup(FakeContentHash('8'), out var e).ShouldBeTrue();
        e!.ChunkSize.ShouldBe(40);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }
}
