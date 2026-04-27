using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.Hashes;

public class ChunkHashTests
{
    [Test]
    public void Parse_NormalizesUppercaseHexToCanonicalLowercase()
    {
        var hash = ChunkHash.Parse("0011CCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899");

        hash.ToString().ShouldBe("0011ccddeeff00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void Prefix4_ReturnsFirstFourHexCharacters()
    {
        var hash = ChunkHash.Parse("0011ccddeeff00112233445566778899aabbccddeeff00112233445566778899");

        hash.Prefix4.ShouldBe("0011");
    }
}
