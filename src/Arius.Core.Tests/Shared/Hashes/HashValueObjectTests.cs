using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.Hashes;

public class HashValueObjectTests
{
    [Test]
    [MatrixDataSource]
    public void Parse_NormalizesUppercaseHexToCanonicalLowercase([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        var uppercase = new string(kind switch
        {
            HashKind.Content  => 'A',
            HashKind.Chunk    => 'B',
            HashKind.FileTree => 'C',
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        }, 64);

        var actual = kind switch
        {
            HashKind.Content  => ContentHash.Parse(uppercase).ToString(),
            HashKind.Chunk    => ChunkHash.Parse(uppercase).ToString(),
            HashKind.FileTree => FileTreeHash.Parse(uppercase).ToString(),
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        actual.ShouldBe(uppercase.ToLowerInvariant());
    }

    [Test]
    [MatrixDataSource]
    public void Parse_RejectsEmptyString([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        Should.Throw<ArgumentException>(() => Parse(kind, string.Empty));
    }

    [Test]
    [MatrixDataSource]
    public void DefaultValue_ToString_ThrowsInvalidOperationException([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        var ex = kind switch
        {
            HashKind.Content  => Should.Throw<InvalidOperationException>(() => default(ContentHash).ToString()),
            HashKind.Chunk    => Should.Throw<InvalidOperationException>(() => default(ChunkHash).ToString()),
            HashKind.FileTree => Should.Throw<InvalidOperationException>(() => default(FileTreeHash).ToString()),
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        ex.Message.ShouldContain(kind switch
        {
            HashKind.Content  => nameof(ContentHash),
            HashKind.Chunk    => nameof(ChunkHash),
            HashKind.FileTree => nameof(FileTreeHash),
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        });
    }

    [Test]
    [MatrixDataSource]
    public void Prefix4_ReturnsFirstFourHexCharacters([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        const string value = "0011ccddeeff00112233445566778899aabbccddeeff00112233445566778899";

        var actual = kind switch
        {
            HashKind.Content  => ContentHash.Parse(value).Prefix4,
            HashKind.Chunk    => ChunkHash.Parse(value).Prefix4,
            HashKind.FileTree => FileTreeHash.Parse(value).Prefix4,
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        actual.ShouldBe("0011");
    }

    [Test]
    [MatrixDataSource]
    public void Short8_ReturnsFirstEightHexCharacters([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        const string value = "ffeeddccbbaa00112233445566778899aabbccddeeff00112233445566778899";

        var actual = kind switch
        {
            HashKind.Content  => ContentHash.Parse(value).Short8,
            HashKind.Chunk    => ChunkHash.Parse(value).Short8,
            HashKind.FileTree => FileTreeHash.Parse(value).Short8,
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        actual.ShouldBe("ffeeddcc");
    }

    [Test]
    [MatrixDataSource]
    public void FromDigest_FormatsCanonicalLowercaseHex([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        var bytes = Convert.FromHexString("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899");

        var actual = kind switch
        {
            HashKind.Content  => ContentHash.FromDigest(bytes).ToString(),
            HashKind.Chunk    => ChunkHash.FromDigest(bytes).ToString(),
            HashKind.FileTree => FileTreeHash.FromDigest(bytes).ToString(),
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        actual.ShouldBe("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void ChunkHashParse_FromContentHash_PreservesCanonicalValue()
    {
        var contentHash = ContentHash.Parse("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899");

        var chunkHash = ChunkHash.Parse(contentHash);

        chunkHash.ToString().ShouldBe("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void ContentHashParse_FromChunkHash_PreservesCanonicalValue()
    {
        var chunkHash = ChunkHash.Parse("BBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899AA");

        var contentHash = ContentHash.Parse(chunkHash);

        contentHash.ToString().ShouldBe("bbccddeeff00112233445566778899aabbccddeeff00112233445566778899aa");
    }

    [Test]
    public void FileTreeHashParse_FromContentHash_PreservesCanonicalValue()
    {
        var contentHash = ContentHash.Parse("CCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899AABB");

        var fileTreeHash = FileTreeHash.Parse(contentHash);

        fileTreeHash.ToString().ShouldBe("ccddeeff00112233445566778899aabbccddeeff00112233445566778899aabb");
    }

    [Test]
    [MatrixDataSource]
    public void Parse_RejectsWrongLength([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        Should.Throw<FormatException>(() => Parse(kind, "abcd"));
    }

    [Test]
    [MatrixDataSource]
    public void Parse_RejectsNonHex([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        Should.Throw<FormatException>(() => Parse(kind, "zzbbccddeeff00112233445566778899aabbccddeeff00112233445566778899"));
    }

    [Test]
    [MatrixDataSource]
    public void EqualHashes_WorkAsDictionaryKeys([Matrix(HashKind.Content, HashKind.Chunk, HashKind.FileTree)] HashKind kind)
    {
        var uppercase = new string(kind switch
        {
            HashKind.Content  => 'A',
            HashKind.Chunk    => 'B',
            HashKind.FileTree => 'C',
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        }, 64);

        var lowercase = uppercase.ToLowerInvariant();

        var actual = kind switch
        {
            HashKind.Content  => LookupByEquivalentKey(ContentHash.Parse(uppercase), ContentHash.Parse(lowercase)),
            HashKind.Chunk    => LookupByEquivalentKey(ChunkHash.Parse(uppercase), ChunkHash.Parse(lowercase)),
            HashKind.FileTree => LookupByEquivalentKey(FileTreeHash.Parse(uppercase), FileTreeHash.Parse(lowercase)),
            _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        actual.ShouldBe("value");

        static string LookupByEquivalentKey<T>(T first, T second) where T : struct
        {
            var dictionary = new Dictionary<T, string>
            {
                [first] = "value"
            };

            return dictionary[second];
        }
    }

    private static object Parse(HashKind kind, string value) => kind switch
    {
        HashKind.Content  => ContentHash.Parse(value),
        HashKind.Chunk    => ChunkHash.Parse(value),
        HashKind.FileTree => FileTreeHash.Parse(value),
        _                 => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public enum HashKind
    {
        Content,
        Chunk,
        FileTree
    }
}
