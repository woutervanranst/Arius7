using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Compression;
using Arius.Tests.Shared;

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

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc, ICompressionService.ZtdInstance);
        var loaded = ShardSerializer.Deserialize(bytes, svc, ICompressionService.ZtdInstance);

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

        var bytes = await ShardSerializer.SerializeAsync(shard, svc, ICompressionService.ZtdInstance);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, svc, ICompressionService.ZtdInstance);

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

        var bytes  = await ShardSerializer.SerializeAsync(shard, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var loaded = ShardSerializer.Deserialize(bytes, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

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

        var bytes = await ShardSerializer.SerializeAsync(shard, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

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
