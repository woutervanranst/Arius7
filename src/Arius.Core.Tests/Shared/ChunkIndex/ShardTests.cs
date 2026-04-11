using Arius.Core.Shared.ChunkIndex;
using Shouldly;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardTests
{
    [Test]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_SmallFile()
    {
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
        var line = "aabbcc00 4200000 1870432";
        var entry = ShardEntry.TryParse(line)!;

        entry.ContentHash.ShouldBe("aabbcc00");
        entry.ChunkHash.ShouldBe("aabbcc00");
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

    [Test]
    public void Shard_WriteToAndReadFrom_RoundTrips()
    {
        var shard = new Shard();
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

        original.TryLookup("hash2", out _).ShouldBeFalse();
    }

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
