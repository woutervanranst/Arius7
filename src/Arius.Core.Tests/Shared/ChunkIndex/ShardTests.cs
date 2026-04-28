using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardTests
{
    [Test]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_SmallFile()
    {
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('d'), 1024, 512);
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
        var entry = new ShardEntry(FakeContentHash('a'), ChunkHash.Parse(FakeContentHash('a')), 4200000, 1870432);
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
        var entry = new ShardEntry(
            ContentHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            ChunkHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            4200000,
            1870432);
        var line  = entry.Serialize();
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        fields.Length.ShouldBe(3);
        fields[0].ShouldBe("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011");
        fields[1].ShouldBe("4200000");
        fields[2].ShouldBe("1870432");
    }

    [Test]
    public void ShardEntry_TryParse_3Fields_ReconstructsChunkHash()
    {
        var hash = "aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011";
        var line = $"{hash} 4200000 1870432";
        var entry = ShardEntry.TryParse(line)!;

        entry.ContentHash.ShouldBe(ContentHash.Parse(hash));
        entry.ChunkHash.ShouldBe(ChunkHash.Parse(hash));
        entry.OriginalSize.ShouldBe(4200000L);
        entry.CompressedSize.ShouldBe(1870432L);
    }

    [Test]
    public void ShardEntry_IsLargeChunk_IsTrueWhenChunkHashMatchesContentHash()
    {
        var entry = new ShardEntry(FakeContentHash('a'), ChunkHash.Parse(FakeContentHash('a')), 4200000, 1870432);

        entry.IsLargeChunk.ShouldBeTrue();
    }

    [Test]
    public void ShardEntry_IsLargeChunk_IsFalseWhenChunkHashDiffersFromContentHash()
    {
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('d'), 1024, 512);

        entry.IsLargeChunk.ShouldBeFalse();
    }

    [Test]
    public void ShardEntry_Serialize_LargeChunk_UsesIsLargeChunkDecision()
    {
        var entry = new ShardEntry(
            ContentHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            ChunkHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            4200000,
            1870432);

        entry.IsLargeChunk.ShouldBeTrue();
        entry.Serialize().ShouldBe("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011 4200000 1870432");
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
            new ShardEntry(
                ContentHash.Parse("aaaa000111111111111111111111111111111111111111111111111111111111"),
                ChunkHash.Parse("bbbb000111111111111111111111111111111111111111111111111111111111"),
                100,
                50),
            new ShardEntry(
                ContentHash.Parse("aaaa000222222222222222222222222222222222222222222222222222222222"),
                ChunkHash.Parse("bbbb000222222222222222222222222222222222222222222222222222222222"),
                200,
                80)
        };
        var populated = shard.Merge(entries);

        var writer = new StringWriter();
        populated.WriteTo(writer);
        var text = writer.ToString();

        var reader  = new StringReader(text);
        var loaded  = Shard.ReadFrom(reader);

        loaded.TryLookup(ContentHash.Parse("aaaa000111111111111111111111111111111111111111111111111111111111"), out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(100);

        loaded.TryLookup(ContentHash.Parse("aaaa000222222222222222222222222222222222222222222222222222222222"), out var e2).ShouldBeTrue();
        e2!.CompressedSize.ShouldBe(80);
    }

    [Test]
    public void Shard_Merge_NewEntriesAddedToExisting()
    {
        var original = new Shard().Merge([new ShardEntry(FakeContentHash('1'), FakeChunkHash('a'), 10, 5)]);
        var merged = original.Merge([new ShardEntry(FakeContentHash('2'), FakeChunkHash('b'), 20, 8)]);

        merged.TryLookup(FakeContentHash('1'), out _).ShouldBeTrue();
        merged.TryLookup(FakeContentHash('2'), out _).ShouldBeTrue();
        merged.Count.ShouldBe(2);
    }

    [Test]
    public void Shard_Merge_DuplicateContentHash_LastWriterWins()
    {
        var contentHash = FakeContentHash('1');
        var original    = new Shard().Merge([new ShardEntry(contentHash, FakeChunkHash('a'), 10, 5)]);
        var merged      = original.Merge([new ShardEntry(contentHash,    FakeChunkHash('b'), 10, 3)]);

        merged.TryLookup(contentHash, out var e).ShouldBeTrue();
        e!.ChunkHash.ShouldBe(FakeChunkHash('b'));
        merged.Count.ShouldBe(1);
    }

    [Test]
    public void Shard_Merge_OriginalIsImmutable()
    {
        var original = new Shard().Merge([new ShardEntry(FakeContentHash('1'), FakeChunkHash('a'), 10, 5)]);
        _ = original.Merge([new ShardEntry(FakeContentHash('2'), FakeChunkHash('b'), 20, 8)]);

        original.TryLookup(FakeContentHash('2'), out _).ShouldBeFalse();
    }

    [Test]
    public void Shard_PrefixOf_Returns4Characters()
    {
        Shard.PrefixOf(ContentHash.Parse("aabbcc1122334455aabbcc1122334455aabbcc1122334455aabbcc1122334455"))
            .ShouldBe("aabb");
    }

    [Test]
    public void ContentHash_Parse_ShortHash_Throws()
    {
        Should.Throw<FormatException>(() => ContentHash.Parse("abc"));
    }
}
