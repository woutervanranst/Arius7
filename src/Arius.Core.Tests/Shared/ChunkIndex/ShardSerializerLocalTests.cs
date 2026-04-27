using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerLocalTests
{
    [Test]
    public void SerializeLocal_ThenDeserializeLocal_RoundTrips()
    {
        var shard = new Shard().Merge([
            new ShardEntry(
                ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"),
                ChunkHash.Parse("ddeeff1100112233445566778899aabbccddeeff00112233445566778899aabb"),
                512,
                256),
            new ShardEntry(
                ContentHash.Parse("11223344556677889900aabbccddeeff00112233445566778899aabbccddeeff"),
                ChunkHash.Parse("556677889900aabbccddeeff00112233445566778899aabbccddeeff00112233"),
                100,
                40)
        ]);

        var bytes  = ShardSerializer.SerializeLocal(shard);
        var loaded = ShardSerializer.DeserializeLocal(bytes);

        loaded.TryLookup(ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"), out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(512);

        loaded.TryLookup(ContentHash.Parse("11223344556677889900aabbccddeeff00112233445566778899aabbccddeeff"), out var e2).ShouldBeTrue();
        e2!.CompressedSize.ShouldBe(40);
    }

    [Test]
    public void SerializeLocal_ProducesHumanReadableText()
    {
        var shard = new Shard().Merge([
            new ShardEntry(
                ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"),
                ChunkHash.Parse("ddeeff1100112233445566778899aabbccddeeff00112233445566778899aabb"),
                512,
                256)
        ]);

        var bytes = ShardSerializer.SerializeLocal(shard);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldContain("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb");
        text.ShouldContain("ddeeff1100112233445566778899aabbccddeeff00112233445566778899aabb");
        text.ShouldContain("512");
        text.ShouldContain("256");
    }

    [Test]
    public void SerializeLocal_IsNotEncryptedOrCompressed()
    {
        var encSvc = new PassphraseEncryptionService("my-passphrase");
        var shard  = new Shard().Merge([
            new ShardEntry(
                ContentHash.Parse("aabbcc00112233445566778899aabbccddeeff00112233445566778899aabb"),
                ChunkHash.Parse("ddeeff1100112233445566778899aabbccddeeff00112233445566778899aabb"),
                512,
                256)
        ]);

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
