namespace Arius.Core.Tests.Shared.FileSystem;

// Pointer files are content-addressed placeholders for archived binaries. v7 stores the bare lowercase hex
// hash; the legacy v5 client stored a JSON object {"BinaryHash":"<hex>"}. The parser must read both so a
// migrated repo's pointer-only files (binary tiered to remote) are not dropped from new snapshots.
public class PointerFileFormatTests
{
    private static readonly string ValidHash = new('a', 64);

    [Test]
    public void TryParseHash_BareHex_IsModern()
    {
        PointerFileFormat.TryParseHash(ValidHash, out var hash, out var isLegacy).ShouldBeTrue();
        hash.ShouldBe(ContentHash.Parse(ValidHash));
        isLegacy.ShouldBeFalse();
    }

    [Test]
    public void TryParseHash_LegacyV5Json_IsParsedAndFlaggedLegacy()
    {
        var content = $"{{\"BinaryHash\":\"{ValidHash}\"}}";

        PointerFileFormat.TryParseHash(content, out var hash, out var isLegacy).ShouldBeTrue();
        hash.ShouldBe(ContentHash.Parse(ValidHash));
        isLegacy.ShouldBeTrue();
    }

    [Test]
    public void TryParseHash_LegacyV5Json_UppercaseHash_IsNormalized()
    {
        var content = $"{{\"BinaryHash\":\"{ValidHash.ToUpperInvariant()}\"}}";

        PointerFileFormat.TryParseHash(content, out var hash, out var isLegacy).ShouldBeTrue();
        hash.ShouldBe(ContentHash.Parse(ValidHash)); // NormalizeHex lowercases A–F
        isLegacy.ShouldBeTrue();
    }

    [Test]
    public void TryParseHash_LegacyV5Json_SurroundingWhitespace_IsParsed()
    {
        var content = $"\n  {{\"BinaryHash\":\"{ValidHash}\"}}  \n";

        PointerFileFormat.TryParseHash(content, out var hash, out var isLegacy).ShouldBeTrue();
        hash.ShouldBe(ContentHash.Parse(ValidHash));
        isLegacy.ShouldBeTrue();
    }

    [Test]
    public void TryParseHash_TwoArgOverload_AlsoAcceptsLegacy()
    {
        var content = $"{{\"BinaryHash\":\"{ValidHash}\"}}";

        PointerFileFormat.TryParseHash(content, out var hash).ShouldBeTrue();
        hash.ShouldBe(ContentHash.Parse(ValidHash));
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-a-hash")]
    [Arguments("{}")]
    [Arguments("{\"Other\":\"value\"}")]
    [Arguments("{\"BinaryHash\":\"too-short\"}")]
    [Arguments("{\"BinaryHash\":null}")]
    [Arguments("{ this is not valid json")]
    public void TryParseHash_InvalidContent_ReturnsFalse(string content)
    {
        PointerFileFormat.TryParseHash(content, out _, out var isLegacy).ShouldBeFalse();
        isLegacy.ShouldBeFalse();
    }

    [Test]
    public void TryParseHash_Null_ReturnsFalse()
    {
        PointerFileFormat.TryParseHash(null, out _, out var isLegacy).ShouldBeFalse();
        isLegacy.ShouldBeFalse();
    }
}
