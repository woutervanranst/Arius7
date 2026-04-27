using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.Hashes;

public class FileTreeHashTests
{
    [Test]
    public void Parse_NormalizesUppercaseHexToCanonicalLowercase()
    {
        var hash = FileTreeHash.Parse("FFEEDDCCBBAA00112233445566778899AABBCCDDEEFF00112233445566778899");

        hash.ToString().ShouldBe("ffeeddccbbaa00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void Short8_ReturnsFirstEightHexCharacters()
    {
        var hash = FileTreeHash.Parse("ffeeddccbbaa00112233445566778899aabbccddeeff00112233445566778899");

        hash.Short8.ShouldBe("ffeeddcc");
    }
}
