using Arius.Core.LocalFile;
using Shouldly;

namespace Arius.Core.Tests.LocalFile;

// ── 7.6 FilePair assembly tests ───────────────────────────────────────────────

public class LocalFileEnumeratorTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileEnumerator _enumerator = new();

    public LocalFileEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arius-enum-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateFile(string relPath, string? content = null)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content ?? "binary-data");
        return full;
    }

    // ── Binary + pointer ──────────────────────────────────────────────────────

    [Test]
    public void Enumerate_BinaryWithPointer_ProducesFilePairWithBoth()
    {
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.pointer.arius", "aabbccdd1122334455667788aabbccdd1122334455667788aabbccdd11223344");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        var pair = pairs[0];
        pair.RelativePath.ShouldBe("photos/vacation.jpg");
        pair.BinaryExists.ShouldBeTrue();
        pair.PointerExists.ShouldBeTrue();
        pair.PointerHash.ShouldNotBeNull();
        pair.FileSize.ShouldNotBeNull();
    }

    // ── Binary only ───────────────────────────────────────────────────────────

    [Test]
    public void Enumerate_BinaryOnly_ProducesFilePairWithNoPointer()
    {
        CreateFile("documents/report.pdf");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe("documents/report.pdf");
        pairs[0].BinaryExists.ShouldBeTrue();
        pairs[0].PointerExists.ShouldBeFalse();
        pairs[0].PointerHash.ShouldBeNull();
    }

    // ── Pointer only (thin archive) ───────────────────────────────────────────

    [Test]
    public void Enumerate_PointerOnly_ProducesOrphanPointerPair()
    {
        var hash = new string('a', 64);
        CreateFile("music/song.mp3.pointer.arius", hash);

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].BinaryExists.ShouldBeFalse();
        pairs[0].PointerExists.ShouldBeTrue();
        pairs[0].PointerHash.ShouldBe(hash);
        pairs[0].FileSize.ShouldBeNull();
    }

    // ── Invalid pointer content ───────────────────────────────────────────────

    [Test]
    public void Enumerate_InvalidPointerContent_PointerHashIsNull()
    {
        CreateFile("docs/file.pdf");
        CreateFile("docs/file.pdf.pointer.arius", "not-a-valid-hex-hash!!");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].PointerExists.ShouldBeTrue();
        pairs[0].PointerHash.ShouldBeNull();
    }

    // ── Stale pointer (hash mismatch handled at higher level, but pair is assembled) ──

    [Test]
    public void Enumerate_StalePointer_PairAssembledWithPointerHash()
    {
        CreateFile("img/photo.png", "actual-binary-data");
        var oldHash = new string('1', 64); // will differ from computed hash
        CreateFile("img/photo.png.pointer.arius", oldHash);

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].BinaryExists.ShouldBeTrue();
        pairs[0].PointerHash.ShouldBe(oldHash);
    }

    // ── Multiple files in nested directories ──────────────────────────────────

    [Test]
    public void Enumerate_MultipleFiles_AllAssembled()
    {
        CreateFile("a.txt");
        CreateFile("photos/b.jpg");
        CreateFile("photos/2024/c.jpg");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(3);
        pairs.Select(p => p.RelativePath).ShouldContain("a.txt");
        pairs.Select(p => p.RelativePath).ShouldContain("photos/b.jpg");
        pairs.Select(p => p.RelativePath).ShouldContain("photos/2024/c.jpg");
    }

    // ── File metadata ─────────────────────────────────────────────────────────

    [Test]
    public void Enumerate_BinaryFile_HasFileSizeAndTimestamps()
    {
        CreateFile("data.bin", "hello world");

        var pair = _enumerator.Enumerate(_root).Single();

        pair.FileSize.ShouldBe(11); // "hello world" = 11 bytes
        pair.Created.ShouldNotBeNull();
        pair.Modified.ShouldNotBeNull();
    }

    // ── 7.7 Path normalization ────────────────────────────────────────────────

    [Test]
    public void NormalizePath_BackslashesToForwardSlashes()
    {
        var result = LocalFileEnumerator.NormalizePath(@"docs\2024\report.pdf");
        result.ShouldBe("docs/2024/report.pdf");
    }

    [Test]
    public void NormalizePath_AlreadyForwardSlash_Unchanged()
    {
        var result = LocalFileEnumerator.NormalizePath("photos/2024/june/a.jpg");
        result.ShouldBe("photos/2024/june/a.jpg");
    }

    [Test]
    public void NormalizePath_LeadingSlash_Removed()
    {
        var result = LocalFileEnumerator.NormalizePath("/photos/file.jpg");
        result.ShouldBe("photos/file.jpg");
    }

    [Test]
    public void NormalizePath_DeeplyNested_CorrectResult()
    {
        var result = LocalFileEnumerator.NormalizePath(@"a\b\c\d\e\f\g.txt");
        result.ShouldBe("a/b/c/d/e/f/g.txt");
    }

    [Test]
    public void Enumerate_UnicodeFilename_ProducesCorrectPath()
    {
        CreateFile("données/résumé.pdf");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe("données/résumé.pdf");
    }

    [Test]
    public void Enumerate_FilenameWithSpaces_ProducesCorrectPath()
    {
        CreateFile("my files/my document.pdf");

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe("my files/my document.pdf");
    }

    [Test]
    public void Enumerate_EmptyDirectory_ProducesNoPairs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-dir"));

        var pairs = _enumerator.Enumerate(_root).ToList();

        pairs.ShouldBeEmpty();
    }
}
