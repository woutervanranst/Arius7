using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ShardTests
{
    [Test]
    [Arguments(BlobTier.Hot)]
    [Arguments(BlobTier.Cool)]
    [Arguments(BlobTier.Cold)]
    [Arguments(BlobTier.Archive)]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_SmallFile(BlobTier tier)
    {
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('d'), 1024, 512, tier);
        var line  = entry.Serialize();
        var back  = ShardEntry.TryParse(line)!;

        back.ContentHash.ShouldBe(entry.ContentHash);
        back.ChunkHash.ShouldBe(entry.ChunkHash);
        back.OriginalSize.ShouldBe(entry.OriginalSize);
        back.ChunkSize.ShouldBe(entry.ChunkSize);
        back.StorageTierHint.ShouldBe(tier);
    }

    [Test]
    [Arguments(BlobTier.Hot)]
    [Arguments(BlobTier.Cool)]
    [Arguments(BlobTier.Cold)]
    [Arguments(BlobTier.Archive)]
    public void ShardEntry_Serialize_ThenParse_RoundTrips_LargeFile(BlobTier tier)
    {
        var entry = new ShardEntry(FakeContentHash('a'), ChunkHash.Parse(FakeContentHash('a')), 4200000, 1870432, tier);
        var line  = entry.Serialize();
        var back  = ShardEntry.TryParse(line)!;

        back.ContentHash.ShouldBe(entry.ContentHash);
        back.ChunkHash.ShouldBe(entry.ChunkHash);
        back.OriginalSize.ShouldBe(entry.OriginalSize);
        back.ChunkSize.ShouldBe(entry.ChunkSize);
        back.StorageTierHint.ShouldBe(tier);
    }

    [Test]
    public void ShardEntry_Serialize_LargeFile_Emits4Fields()
    {
        var entry = new ShardEntry(
            ContentHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            ChunkHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            4200000,
            1870432,
            BlobTier.Archive);
        var line  = entry.Serialize();
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        fields.Length.ShouldBe(4);
        fields[0].ShouldBe("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011");
        fields[1].ShouldBe("4200000");
        fields[2].ShouldBe("1870432");
        fields[3].ShouldBe("4");
    }

    [Test]
    public void ShardEntry_TryParse_4Fields_ReconstructsChunkHash()
    {
        var hash = "aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011";
        var line = $"{hash} 4200000 1870432 1";
        var entry = ShardEntry.TryParse(line)!;

        entry.ContentHash.ShouldBe(ContentHash.Parse(hash));
        entry.ChunkHash.ShouldBe(ChunkHash.Parse(hash));
        entry.OriginalSize.ShouldBe(4200000L);
        entry.ChunkSize.ShouldBe(1870432L);
        entry.StorageTierHint.ShouldBe(BlobTier.Hot);
    }

    [Test]
    public void ShardEntry_TierWireMapping_IsStable()
    {
        // The wire values are part of the shard format and must never follow enum reordering.
        ShardEntry.SerializeTier(BlobTier.Hot).ShouldBe(1);
        ShardEntry.SerializeTier(BlobTier.Cool).ShouldBe(2);
        ShardEntry.SerializeTier(BlobTier.Cold).ShouldBe(3);
        ShardEntry.SerializeTier(BlobTier.Archive).ShouldBe(4);

        ShardEntry.DeserializeTier(1).ShouldBe(BlobTier.Hot);
        ShardEntry.DeserializeTier(2).ShouldBe(BlobTier.Cool);
        ShardEntry.DeserializeTier(3).ShouldBe(BlobTier.Cold);
        ShardEntry.DeserializeTier(4).ShouldBe(BlobTier.Archive);

        Should.Throw<FormatException>(() => ShardEntry.DeserializeTier(0));
        Should.Throw<FormatException>(() => ShardEntry.DeserializeTier(5));
    }

    [Test]
    public void ShardEntry_IsLargeChunk_IsTrueWhenChunkHashMatchesContentHash()
    {
        var entry = new ShardEntry(FakeContentHash('a'), ChunkHash.Parse(FakeContentHash('a')), 4200000, 1870432, BlobTier.Hot);

        entry.IsLargeChunk.ShouldBeTrue();
    }

    [Test]
    public void ShardEntry_IsLargeChunk_IsFalseWhenChunkHashDiffersFromContentHash()
    {
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('d'), 1024, 512, BlobTier.Hot);

        entry.IsLargeChunk.ShouldBeFalse();
    }

    [Test]
    public void ShardEntry_Serialize_LargeChunk_UsesIsLargeChunkDecision()
    {
        var entry = new ShardEntry(
            ContentHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            ChunkHash.Parse("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011"),
            4200000,
            1870432,
            BlobTier.Cool);

        entry.IsLargeChunk.ShouldBeTrue();
        entry.Serialize().ShouldBe("aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011 4200000 1870432 2");
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
        Should.Throw<FormatException>(() => ShardEntry.TryParse("a b c d e f"));

        // Legacy tier-less 3-field large-file lines are no longer supported.
        Should.Throw<FormatException>(() => ShardEntry.TryParse(
            "aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011 4200000 1870432"));

        // Invalid tier wire value.
        Should.Throw<FormatException>(() => ShardEntry.TryParse(
            "aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011aabbccddeeff0011 4200000 1870432 9"));
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
                50,
                BlobTier.Archive),
            new ShardEntry(
                ContentHash.Parse("aaaa000222222222222222222222222222222222222222222222222222222222"),
                ChunkHash.Parse("bbbb000222222222222222222222222222222222222222222222222222222222"),
                200,
                80,
                BlobTier.Cool)
        };
        shard.AddOrUpdateRange(entries);

        var writer = new StringWriter();
        shard.WriteTo(writer);
        var text = writer.ToString();

        var reader  = new StringReader(text);
        var loaded  = Shard.ReadFrom(reader);

        loaded.TryLookup(ContentHash.Parse("aaaa000111111111111111111111111111111111111111111111111111111111"), out var e1).ShouldBeTrue();
        e1!.OriginalSize.ShouldBe(100);
        e1.StorageTierHint.ShouldBe(BlobTier.Archive);

        loaded.TryLookup(ContentHash.Parse("aaaa000222222222222222222222222222222222222222222222222222222222"), out var e2).ShouldBeTrue();
        e2!.ChunkSize.ShouldBe(80);
        e2.StorageTierHint.ShouldBe(BlobTier.Cool);
    }

    [Test]
    public void Shard_AddOrUpdateRange_NewEntriesAddedToExisting()
    {
        var shard = new Shard();
        shard.AddOrUpdate(new ShardEntry(FakeContentHash('1'), FakeChunkHash('a'), 10, 5, BlobTier.Hot));
        shard.AddOrUpdate(new ShardEntry(FakeContentHash('2'), FakeChunkHash('b'), 20, 8, BlobTier.Hot));

        shard.TryLookup(FakeContentHash('1'), out _).ShouldBeTrue();
        shard.TryLookup(FakeContentHash('2'), out _).ShouldBeTrue();
        shard.Count.ShouldBe(2);
    }

    [Test]
    public void Shard_AddOrUpdateRange_DuplicateContentHash_LastWriterWins()
    {
        var contentHash = FakeContentHash('1');
        var shard = new Shard();
        shard.AddOrUpdateRange([
            new ShardEntry(contentHash, FakeChunkHash('a'), 10, 5, BlobTier.Hot),
            new ShardEntry(contentHash, FakeChunkHash('b'), 10, 3, BlobTier.Hot),
        ]);

        shard.TryLookup(contentHash, out var e).ShouldBeTrue();
        e!.ChunkHash.ShouldBe(FakeChunkHash('b'));
        shard.Count.ShouldBe(1);
    }

    [Test]
    public void Shard_PrefixOf_UsesChunkIndexShardPrefixLength()
    {
        Shard.PrefixOf(ContentHash.Parse("aabbcc1122334455aabbcc1122334455aabbcc1122334455aabbcc1122334455"))
            .ShouldBe(PathSegment.Parse("aa"));
    }

    [Test]
    public void ContentHash_Parse_ShortHash_Throws()
    {
        Should.Throw<FormatException>(() => ContentHash.Parse("abc"));
    }
}
