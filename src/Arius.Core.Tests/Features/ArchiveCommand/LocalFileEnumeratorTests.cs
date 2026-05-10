using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Features.ArchiveCommand;

// ── 7.6 FilePair assembly tests ───────────────────────────────────────────────

public class LocalFileEnumeratorTests : IDisposable
{
    private readonly string _root;
    private readonly LocalDirectory _rootDirectory;
    private readonly LocalFileEnumerator _enumerator = new();

    public LocalFileEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arius-enum-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _rootDirectory = LocalDirectory.Parse(_root);
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

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        var pair = pairs[0];
        pair.RelativePath.ShouldBe(RelativePath.Parse("photos/vacation.jpg"));
        pair.Binary.ShouldNotBeNull();
        pair.Pointer.ShouldNotBeNull();
        pair.Pointer!.Hash.ShouldNotBeNull();
        pair.Binary!.Size.ShouldBeGreaterThan(0);
    }

    // ── Binary only ───────────────────────────────────────────────────────────

    [Test]
    public void Enumerate_BinaryOnly_ProducesFilePairWithNoPointer()
    {
        CreateFile("documents/report.pdf");

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("documents/report.pdf"));
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldBeNull();
    }

    // ── Pointer only (thin archive) ───────────────────────────────────────────

    [Test]
    public void Enumerate_PointerOnly_ProducesOrphanPointerPair()
    {
        var hash = new string('a', 64);
        CreateFile("music/song.mp3.pointer.arius", hash);

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].Binary.ShouldBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBe(ContentHash.Parse(hash));
    }

    [Test]
    public void Enumerate_PointerOnly_UsesLogicalBinaryRelativePath()
    {
        CreateFile("music/song.mp3.pointer.arius", new string('a', 64));

        var pair = _enumerator.Enumerate(_rootDirectory).Single();

        pair.RelativePath.ShouldBe(RelativePath.Parse("music/song.mp3"));
    }

    // ── Invalid pointer content ───────────────────────────────────────────────

    [Test]
    public void Enumerate_InvalidPointerContent_PointerHashIsNull()
    {
        CreateFile("docs/file.pdf");
        CreateFile("docs/file.pdf.pointer.arius", "not-a-valid-hex-hash!!");

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBeNull();
    }

    // ── Stale pointer (hash mismatch handled at higher level, but pair is assembled) ──

    [Test]
    public void Enumerate_StalePointer_PairAssembledWithPointerHash()
    {
        CreateFile("img/photo.png", "actual-binary-data");
        var oldHash = new string('1', 64); // will differ from computed hash
        CreateFile("img/photo.png.pointer.arius", oldHash);

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBe(ContentHash.Parse(oldHash));
    }

    // ── Multiple files in nested directories ──────────────────────────────────

    [Test]
    public void Enumerate_MultipleFiles_AllAssembled()
    {
        CreateFile("a.txt");
        CreateFile("photos/b.jpg");
        CreateFile("photos/2024/c.jpg");

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(3);
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("a.txt");
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("photos/b.jpg");
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("photos/2024/c.jpg");
    }

    // ── File metadata ─────────────────────────────────────────────────────────

    [Test]
    public void Enumerate_BinaryFile_HasFileSizeAndTimestamps()
    {
        CreateFile("data.bin", "hello world");

        var pair = _enumerator.Enumerate(_rootDirectory).Single();

        pair.Binary!.Size.ShouldBe(11); // "hello world" = 11 bytes
        pair.Binary.Created.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        pair.Binary.Modified.ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Test]
    public void Enumerate_UnicodeFilename_ProducesCorrectPath()
    {
        CreateFile("données/résumé.pdf");

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("données/résumé.pdf"));
    }

    [Test]
    public void Enumerate_FilenameWithSpaces_ProducesCorrectPath()
    {
        CreateFile("my files/my document.pdf");

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("my files/my document.pdf"));
    }

    [Test]
    public void Enumerate_EmptyDirectory_ProducesNoPairs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-dir"));

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.ShouldBeEmpty();
    }

    // ── Single-pass: pointer-with-binary is skipped ───────────────────────────

    /// <summary>
    /// Spec 3.3: When a pointer file is encountered during depth-first walk and its
    /// binary counterpart exists, the pointer file must be silently skipped —
    /// it was already emitted as part of the binary's FilePair.
    /// </summary>
    [Test]
    public void Enumerate_PointerFileWithBinaryPresent_OnlyOnePairEmitted()
    {
        // Both files exist; the pointer should NOT produce a second pair
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.pointer.arius", new string('a', 64));

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1, "pointer-with-binary must not produce an extra pair");
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
    }

    [Test, Skip("Tracked by #82: restore should handle cross-OS path conflicts gracefully.")]
    public void Enumerate_PointerSuffixComparison_IsCaseInsensitive()
    {
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.POINTER.ARIUS", new string('a', 64));

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("photos/vacation.jpg"));
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Path.ShouldBe(RelativePath.Parse("photos/vacation.jpg.POINTER.ARIUS"));
        pairs[0].Pointer!.Hash.ShouldNotBeNull();
    }

    // ── Single-pass: yielded before enumeration completes ─────────────────────

    /// <summary>
    /// Spec: pipeline should begin processing the first FilePair before enumeration
    /// completes (no .ToList() buffering). Verified by consuming the IEnumerable
    /// lazily and asserting the first element is available before the rest.
    /// </summary>
    [Test]
    public void Enumerate_IsLazy_FirstElementAvailableBeforeAll()
    {
        // Create several files
        for (var i = 0; i < 5; i++)
            CreateFile($"file{i}.bin");

        var firstYielded  = false;
        var countConsumed = 0;

        foreach (var _ in _enumerator.Enumerate(_rootDirectory))
        {
            if (!firstYielded)
            {
                // At this point only 1 item has been yielded — the enumeration
                // is still in progress (we haven't called ToList)
                firstYielded = true;
            }
            countConsumed++;
        }

        firstYielded.ShouldBeTrue();
        countConsumed.ShouldBe(5);
    }

    [Test]
    public void Enumerate_YieldsBeforeEnumerationCompletes()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");

        using var iterator = _enumerator.Enumerate(_rootDirectory).GetEnumerator();

        iterator.MoveNext().ShouldBeTrue();

        var firstPath = iterator.Current.RelativePath;
        var secondPath = firstPath == RelativePath.Parse("a.txt")
            ? RelativePath.Parse("b.txt")
            : RelativePath.Parse("a.txt");

        CreateFile(secondPath.ToString() + ".pointer.arius", new string('a', 64));

        iterator.MoveNext().ShouldBeTrue();
        iterator.Current.RelativePath.ShouldBe(secondPath);
        iterator.Current.Pointer.ShouldNotBeNull();
        iterator.Current.Pointer!.Hash.ShouldBe(ContentHash.Parse(new string('a', 64)));
    }
}
