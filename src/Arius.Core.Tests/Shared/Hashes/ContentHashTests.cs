using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.Hashes;

public class ContentHashTests
{
    [Test]
    public void Parse_NormalizesUppercaseHexToCanonicalLowercase()
    {
        var hash = ContentHash.Parse("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899");

        hash.ToString().ShouldBe("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void Parse_RejectsNonHex()
    {
        Should.Throw<FormatException>(() =>
            ContentHash.Parse("zzbbccddeeff00112233445566778899aabbccddeeff00112233445566778899"));
    }

    [Test]
    public void Parse_RejectsWrongLength()
    {
        Should.Throw<FormatException>(() => ContentHash.Parse("abcd"));
    }

    [Test]
    public void FromDigest_FormatsCanonicalLowercaseHex()
    {
        var bytes = Convert.FromHexString("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899");

        var hash = ContentHash.FromDigest(bytes);

        hash.ToString().ShouldBe("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");
    }

    [Test]
    public void DefaultValue_ToString_ThrowsInvalidOperationException()
    {
        var hash = default(ContentHash);

        var ex = Should.Throw<InvalidOperationException>(() => hash.ToString());
        ex.Message.ShouldContain("ContentHash");
    }

    [Test]
    public void Parse_RejectsEmptyString()
    {
        Should.Throw<ArgumentException>(() => ContentHash.Parse(string.Empty));
    }
}
