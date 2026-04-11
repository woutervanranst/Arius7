using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Shouldly;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_WithPassphrase_RoundTrips()
    {
        var svc   = new PassphraseEncryptionService("my-passphrase");
        var shard = new Shard().Merge([new ShardEntry("aabbcc00", "ddeeff11", 512, 256)]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup("aabbcc00", out var e).ShouldBeTrue();
        e!.OriginalSize.ShouldBe(512);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var svc   = new PlaintextPassthroughService();
        var shard = new Shard().Merge([new ShardEntry("11223344", "55667788", 100, 40)]);

        var bytes  = await ShardSerializer.SerializeAsync(shard, svc);
        var loaded = ShardSerializer.Deserialize(bytes, svc);

        loaded.TryLookup("11223344", out var e).ShouldBeTrue();
        e!.CompressedSize.ShouldBe(40);
    }
}
