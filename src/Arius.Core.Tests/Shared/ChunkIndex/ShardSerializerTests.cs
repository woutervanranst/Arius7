using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_WithPassphrase_RoundTrips()
    {
        var svc   = new PassphraseEncryptionService("my-passphrase");
        var shard = new Shard().Merge([
            new ShardEntry(
                ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"),
                ChunkHash.Parse("ddeeff1100112233445566778899aabbccddeeff00112233445566778899aabb"),
                512,
                256)
        ]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var svc   = new PlaintextPassthroughService();
        var shard = new Shard().Merge([
            new ShardEntry(
                ContentHash.Parse("11223344556677889900aabbccddeeff00112233445566778899aabbccddeeff"),
                ChunkHash.Parse("556677889900aabbccddeeff00112233445566778899aabbccddeeff00112233"),
                100,
                40)
        ]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(ContentHash.Parse("11223344556677889900aabbccddeeff00112233445566778899aabbccddeeff"), out var e).ShouldBeTrue();
        e!.CompressedSize.ShouldBe(40);
    }
}
