using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_WithPassphrase_RoundTrips()
    {
        var svc   = new PassphraseEncryptionService("my-passphrase");
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('a'),
                FakeChunkHash('b'),
                512,
                256,
                BlobTier.Archive)
        );

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(FakeContentHash('a'), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
        e.StorageTierHint.ShouldBe(BlobTier.Archive);
    }

    [Test]
    public async Task Serialize_ThenDeserializeStream_WithPassphrase_RoundTrips()
    {
        var svc   = new PassphraseEncryptionService("my-passphrase");
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('e'),
                FakeChunkHash('f'),
                512,
                256,
                BlobTier.Archive)
        );

        var bytes = await ShardSerializer.SerializeAsync(shard, svc);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, svc);

        loaded.TryLookup(FakeContentHash('e'), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var svc   = new PlaintextPassthroughService();
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('c'),
                FakeChunkHash('d'),
                100,
                40,
                BlobTier.Cool)
        );

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(FakeContentHash('c'), out var e).ShouldBeTrue();
        e!.CompressedSize.ShouldBe(40);
        e.StorageTierHint.ShouldBe(BlobTier.Cool);
    }

    [Test]
    public async Task Serialize_ThenDeserializeStream_Plaintext_RoundTrips()
    {
        var svc   = new PlaintextPassthroughService();
        var shard = CreateShard(
            new ShardEntry(
                FakeContentHash('8'),
                FakeChunkHash('9'),
                100,
                40,
                BlobTier.Cool)
        );

        var bytes = await ShardSerializer.SerializeAsync(shard, svc);
        using var stream = new MemoryStream(bytes);
        var loaded = ShardSerializer.Deserialize(stream, svc);

        loaded.TryLookup(FakeContentHash('8'), out var e).ShouldBeTrue();
        e!.CompressedSize.ShouldBe(40);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }
}
