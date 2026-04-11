using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Shouldly;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerLocalTests
{
    [Test]
    public void SerializeLocal_ThenDeserializeLocal_RoundTrips()
    {
        var shard = new Shard().Merge([
            new ShardEntry("aabbcc00", "ddeeff11", 512, 256),
            new ShardEntry("11223344", "55667788", 100, 40)
        ]);

        var bytes  = ShardSerializer.SerializeLocal(shard);
        var loaded = ShardSerializer.DeserializeLocal(bytes);

        loaded.TryLookup("aabbcc00", out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(512);

        loaded.TryLookup("11223344", out var e2).ShouldBeTrue();
        e2!.CompressedSize.ShouldBe(40);
    }

    [Test]
    public void SerializeLocal_ProducesHumanReadableText()
    {
        var shard = new Shard().Merge([new ShardEntry("aabbcc00", "ddeeff11", 512, 256)]);

        var bytes = ShardSerializer.SerializeLocal(shard);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldContain("aabbcc00");
        text.ShouldContain("ddeeff11");
        text.ShouldContain("512");
        text.ShouldContain("256");
    }

    [Test]
    public void SerializeLocal_IsNotEncryptedOrCompressed()
    {
        var encSvc = new PassphraseEncryptionService("my-passphrase");
        var shard  = new Shard().Merge([new ShardEntry("aabbcc00", "ddeeff11", 512, 256)]);

        var localBytes = ShardSerializer.SerializeLocal(shard);

        var salted = System.Text.Encoding.ASCII.GetBytes("Salted__");
        localBytes.Take(8).ShouldNotBe(salted);
        localBytes[0].ShouldNotBe((byte)0x1f);
    }

    [Test]
    public void DeserializeLocal_EmptyShard_RoundTrips()
    {
        var shard  = new Shard();
        var bytes  = ShardSerializer.SerializeLocal(shard);
        var loaded = ShardSerializer.DeserializeLocal(bytes);
        loaded.Count.ShouldBe(0);
    }
}
