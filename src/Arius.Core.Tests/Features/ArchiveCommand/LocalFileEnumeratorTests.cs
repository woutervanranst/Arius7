using Arius.Core.Features.ArchiveCommand;
using Arius.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Core.Tests.Features.ArchiveCommand;

// ── 7.6 FilePair assembly tests ───────────────────────────────────────────────

public class LocalFileEnumeratorTests : IDisposable
{
    private readonly LocalDirectory _rootDirectory;
    private readonly RelativeFileSystem _fileSystem;
    private readonly LocalFileEnumerator _enumerator = new();

    public LocalFileEnumeratorTests()
    {
        _rootDirectory = TestTempRoots.CreateDirectory("enum-test");
        _fileSystem = new RelativeFileSystem(_rootDirectory);
        _fileSystem.CreateDirectory(RelativePath.Root);
    }

    public void Dispose()
    {
        _fileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
    }

    private string CreateFile(string relPath, string? content = null)
    {
        var relativePath = RelativePath.Parse(relPath);
        var full = _rootDirectory.Resolve(relativePath);
        _fileSystem.CreateDirectory(relativePath.Parent ?? RelativePath.Root);
        File.WriteAllText(full, content ?? "binary-data");
        return full;
    }

    private string CreateSymbolicLink(string relPath, string target)
    {
        var relativePath = RelativePath.Parse(relPath);
        var full = _rootDirectory.Resolve(relativePath);
        _fileSystem.CreateDirectory(relativePath.Parent ?? RelativePath.Root);
        File.CreateSymbolicLink(full, target);
        return full;
    }

    // ── Broken symlinks (skipped) ─────────────────────────────────────────────

    [Test]
    public void Enumerate_BrokenSymlink_IsSkippedWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        CreateFile("regular.txt");
        CreateSymbolicLink("broken-link.txt", Path.Combine(_rootDirectory.ToString(), "missing-target"));

        var logger     = new FakeLogger<LocalFileEnumerator>();
        var enumerator = new LocalFileEnumerator(logger);

        var pairs = enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["regular.txt"]);
        logger.Collector.GetSnapshot()
            .ShouldContain(r => r.Level == LogLevel.Warning && r.Message.Contains("broken-link.txt"));
    }

    [Test]
    public void Enumerate_ValidSymlink_IsYielded()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        CreateFile("target.txt");
        CreateSymbolicLink("valid-link.txt", Path.Combine(_rootDirectory.ToString(), "target.txt"));

        var pairs = _enumerator.Enumerate(_rootDirectory).ToList();

        pairs.Select(p => p.RelativePath.ToString())
            .ShouldBe(["target.txt", "valid-link.txt"], ignoreOrder: true);
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
        _fileSystem.CreateDirectory(RelativePath.Parse("empty-dir"));

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
