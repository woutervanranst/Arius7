using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_WithPassphrase_RoundTrips()
    {
        var svc   = new PassphraseEncryptionService("my-passphrase");
        var shard = new Shard().Merge([
            new ShardEntry(
                HashTestData.Content('a'),
                HashTestData.Chunk('b'),
                512,
                256)
        ]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(HashTestData.Content('a'), out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var svc   = new PlaintextPassthroughService();
        var shard = new Shard().Merge([
            new ShardEntry(
                HashTestData.Content('c'),
                HashTestData.Chunk('d'),
                100,
                40)
        ]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup(HashTestData.Content('c'), out var e).ShouldBeTrue();
        e!.CompressedSize.ShouldBe(40);
    }
}
