using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardSerializerLocalTests
{
    private static ContentHash Content(char c) => ContentHash.Parse(new string(c, 64));
    private static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

    [Test]
    public void SerializeLocal_ThenDeserializeLocal_RoundTrips()
    {
        var shard = new Shard().Merge([
            new ShardEntry(
                Content('a'),
                Chunk('b'),
                512,
                256),
            new ShardEntry(
                Content('c'),
                Chunk('d'),
                100,
                40)
        ]);

        var bytes  = ShardSerializer.SerializeLocal(shard);
        var loaded = ShardSerializer.DeserializeLocal(bytes);

        loaded.TryLookup(Content('a'), out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(512);

        loaded.TryLookup(Content('c'), out var e2).ShouldBeTrue();
        e2!.CompressedSize.ShouldBe(40);
    }

    [Test]
    public void SerializeLocal_ProducesHumanReadableText()
    {
        var shard = new Shard().Merge([
            new ShardEntry(
                Content('a'),
                Chunk('b'),
                512,
                256)
        ]);

        var bytes = ShardSerializer.SerializeLocal(shard);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldContain(new string('a', 64));
        text.ShouldContain(new string('b', 64));
        text.ShouldContain("512");
        text.ShouldContain("256");
    }

    [Test]
    public void SerializeLocal_IsNotEncryptedOrCompressed()
    {
        var encSvc = new PassphraseEncryptionService("my-passphrase");
        var shard  = new Shard().Merge([
            new ShardEntry(
                Content('a'),
                Chunk('b'),
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
