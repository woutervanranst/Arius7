using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Shouldly;

namespace Arius.Core.Tests.ChunkIndex;

public class ShardTests
{
    // ── 4.1 Shard entry parse/serialize roundtrip ─────────────────────────────

    [Test]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_SmallFile()
    {
        // Small file: content-hash != chunk-hash → 4 fields
        var entry = new ShardEntry("aabbcc00", "ddeeff11", 1024, 512);
        var line  = entry.Serialize();
        var back  = ShardEntry.TryParse(line)!;

        back.ContentHash.ShouldBe(entry.ContentHash);
        back.ChunkHash.ShouldBe(entry.ChunkHash);
        back.OriginalSize.ShouldBe(entry.OriginalSize);
        back.CompressedSize.ShouldBe(entry.CompressedSize);
    }

    [Test]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_LargeFile()
    {
        // Large file: content-hash == chunk-hash → 3 fields
        var entry = new ShardEntry("aabbcc00", "aabbcc00", 4200000, 1870432);
        var line  = entry.Serialize();
        var back  = ShardEntry.TryParse(line)!;

        back.ContentHash.ShouldBe(entry.ContentHash);
        back.ChunkHash.ShouldBe(entry.ChunkHash);
        back.OriginalSize.ShouldBe(entry.OriginalSize);
        back.CompressedSize.ShouldBe(entry.CompressedSize);
    }

    [Test]
    public void ShardEntry_Serialize_LargeFile_Emits3Fields()
    {
        // content-hash == chunk-hash → only 3 fields emitted (no redundant chunk-hash)
        var entry = new ShardEntry("aabbcc00", "aabbcc00", 4200000, 1870432);
        var line  = entry.Serialize();
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        fields.Length.ShouldBe(3);
        fields[0].ShouldBe("aabbcc00");
        fields[1].ShouldBe("4200000");
        fields[2].ShouldBe("1870432");
    }

    [Test]
    public void ShardEntry_TryParse_3Fields_ReconstructsChunkHash()
    {
        // 3-field line → large file, chunk-hash reconstructed as content-hash
        var line = "aabbcc00 4200000 1870432";
        var entry = ShardEntry.TryParse(line)!;

        entry.ContentHash.ShouldBe("aabbcc00");
        entry.ChunkHash.ShouldBe("aabbcc00");   // reconstructed
        entry.OriginalSize.ShouldBe(4200000L);
        entry.CompressedSize.ShouldBe(1870432L);
    }

    [Test]
    public void ShardEntry_TryParse_BlankLine_ReturnsNull()
    {
        ShardEntry.TryParse("").ShouldBeNull();
        ShardEntry.TryParse("   ").ShouldBeNull();
        ShardEntry.TryParse("# comment").ShouldBeNull();
    }

    [Test]
    public void ShardEntry_TryParse_InvalidLine_Throws()
    {
        Should.Throw<FormatException>(() => ShardEntry.TryParse("only-one-field"));
        Should.Throw<FormatException>(() => ShardEntry.TryParse("field1 field2"));
        Should.Throw<FormatException>(() => ShardEntry.TryParse("a b c d e"));
    }

    // ── 4.1 Shard serialization roundtrip ────────────────────────────────────

    [Test]
    public void Shard_WriteToAndReadFrom_RoundTrips()
    {
        var shard = new Shard();
        // Populate via Merge with entries
        var entries = new[]
        {
            new ShardEntry("aaaa0001", "bbbb0001", 100, 50),
            new ShardEntry("aaaa0002", "bbbb0002", 200, 80)
        };
        var populated = shard.Merge(entries);

        var writer = new StringWriter();
        populated.WriteTo(writer);
        var text = writer.ToString();

        var reader  = new StringReader(text);
        var loaded  = Shard.ReadFrom(reader);

        loaded.TryLookup("aaaa0001", out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(100);

        loaded.TryLookup("aaaa0002", out var e2).ShouldBeTrue();
        e2!.CompressedSize.ShouldBe(80);
    }

    // ── 4.2 Merge correctness ────────────────────────────────────────────────

    [Test]
    public void Shard_Merge_NewEntriesAddedToExisting()
    {
        var original = new Shard().Merge([new ShardEntry("hash1", "chunk1", 10, 5)]);
        var merged   = original.Merge([new ShardEntry("hash2", "chunk2", 20, 8)]);

        merged.TryLookup("hash1", out _).ShouldBeTrue();
        merged.TryLookup("hash2", out _).ShouldBeTrue();
        merged.Count.ShouldBe(2);
    }

    [Test]
    public void Shard_Merge_DuplicateContentHash_LastWriterWins()
    {
        var original = new Shard().Merge([new ShardEntry("hash1", "old-chunk", 10, 5)]);
        var merged   = original.Merge([new ShardEntry("hash1", "new-chunk", 10, 3)]);

        merged.TryLookup("hash1", out var e).ShouldBeTrue();
        e!.ChunkHash.ShouldBe("new-chunk");
        merged.Count.ShouldBe(1);
    }

    [Test]
    public void Shard_Merge_OriginalIsImmutable()
    {
        var original = new Shard().Merge([new ShardEntry("hash1", "chunk1", 10, 5)]);
        _ = original.Merge([new ShardEntry("hash2", "chunk2", 20, 8)]);

        // Original shard should not have "hash2"
        original.TryLookup("hash2", out _).ShouldBeFalse();
    }

    // ── 4.9 Prefix derivation ────────────────────────────────────────────────

    [Test]
    public void Shard_PrefixOf_Returns4Characters()
    {
        Shard.PrefixOf("aabbcc1122334455aabbcc1122334455aabbcc1122334455aabbcc1122334455")
            .ShouldBe("aabb");
    }

    [Test]
    public void Shard_PrefixOf_ShortHash_Throws()
    {
        Should.Throw<ArgumentException>(() => Shard.PrefixOf("abc"));
    }
}

public class ShardSerializerTests
{
    // ── 4.3 Gzip compression roundtrip ───────────────────────────────────────

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

public class ShardSerializerLocalTests
{
    // ── Local (plaintext) serialize/deserialize roundtrip ────────────────────

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

        // Local bytes must NOT start with the AES "Salted__" prefix
        var salted = System.Text.Encoding.ASCII.GetBytes("Salted__");
        localBytes.Take(8).ShouldNotBe(salted);

        // Local bytes must NOT start with the gzip magic bytes (1f 8b)
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

public class ChunkIndexServiceTests
{
    [Test]
    public void RepoDirectoryName_Format_IsAccountHyphenContainer()
    {
        var name = ChunkIndexService.GetRepoDirectoryName("mystorageacct", "photos");
        name.ShouldBe("mystorageacct-photos");
    }

    [Test]
    public void RepoDirectoryName_DifferentContainers_ProduceDifferentNames()
    {
        var n1 = ChunkIndexService.GetRepoDirectoryName("account", "container1");
        var n2 = ChunkIndexService.GetRepoDirectoryName("account", "container2");
        n1.ShouldNotBe(n2);
    }

    [Test]
    public void RepoDirectoryName_SameInputs_ProduceSameResult()
    {
        var n1 = ChunkIndexService.GetRepoDirectoryName("account", "container");
        var n2 = ChunkIndexService.GetRepoDirectoryName("account", "container");
        n1.ShouldBe(n2);
    }
}
