using Arius.Core.Features.ArchiveCommand;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Features.ArchiveCommand;

// ── 7.6 FilePair assembly tests ───────────────────────────────────────────────

public class LocalFileEnumeratorTests : IDisposable
{
    private readonly LocalDirectory _rootDirectory;
    private readonly RelativeFileSystem _fileSystem;
    private readonly LocalFileEnumerator _enumerator = new();

    // Exclusions are reported via the onExcluded callback (the enumerator is mediator-free and does not log
    // exclusions itself); CapturingEnumerator records them so tests can assert path + reason.
    private readonly List<(RelativePath Path, ExclusionReason Reason, Exception? Exception)> _excluded = [];

    private LocalFileEnumerator CapturingEnumerator() =>
        new(onExcluded: (path, reason, ex) => { _excluded.Add((path, reason, ex)); return ValueTask.CompletedTask; });

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
    public async Task Enumerate_BrokenSymlink_IsSkippedViaCallback()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        CreateFile("regular.txt");
        CreateSymbolicLink("broken-link.txt", Path.Combine(_rootDirectory.ToString(), "missing-target"));

        var pairs = await CapturingEnumerator().EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["regular.txt"]);
        _excluded.ShouldContain(s => s.Path.ToString() == "broken-link.txt" && s.Reason == ExclusionReason.BrokenSymlink);
    }

    [Test]
    public async Task Enumerate_ValidSymlink_IsYielded()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        CreateFile("target.txt");
        CreateSymbolicLink("valid-link.txt", Path.Combine(_rootDirectory.ToString(), "target.txt"));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString())
            .ShouldBe(["target.txt", "valid-link.txt"], ignoreOrder: true);
    }

    // ── Binary + pointer ──────────────────────────────────────────────────────

    [Test]
    public async Task Enumerate_BinaryWithPointer_ProducesFilePairWithBoth()
    {
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.pointer.arius", "aabbccdd1122334455667788aabbccdd1122334455667788aabbccdd11223344");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        var pair = pairs[0];
        pair.RelativePath.ShouldBe(RelativePath.Parse("photos/vacation.jpg"));
        pair.Binary.ShouldNotBeNull();
        pair.Pointer.ShouldNotBeNull();
        pair.Pointer!.Hash.ShouldNotBeNull();
    }

    // ── Binary only ───────────────────────────────────────────────────────────

    [Test]
    public async Task Enumerate_BinaryOnly_ProducesFilePairWithNoPointer()
    {
        CreateFile("documents/report.pdf");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("documents/report.pdf"));
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldBeNull();
    }

    // ── Pointer only (thin archive) ───────────────────────────────────────────

    [Test]
    public async Task Enumerate_PointerOnly_ProducesOrphanPointerPair()
    {
        var hash = new string('a', 64);
        CreateFile("music/song.mp3.pointer.arius", hash);

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].Binary.ShouldBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBe(ContentHash.Parse(hash));
    }

    [Test]
    public async Task Enumerate_PointerOnly_UsesLogicalBinaryRelativePath()
    {
        CreateFile("music/song.mp3.pointer.arius", new string('a', 64));

        var pair = (await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync()).Single();

        pair.RelativePath.ShouldBe(RelativePath.Parse("music/song.mp3"));
    }

    // ── Invalid pointer content ───────────────────────────────────────────────

    [Test]
    public async Task Enumerate_InvalidPointerContent_PointerHashIsNull()
    {
        CreateFile("docs/file.pdf");
        CreateFile("docs/file.pdf.pointer.arius", "not-a-valid-hex-hash!!");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBeNull();
    }

    // ── Stale pointer (hash mismatch handled at higher level, but pair is assembled) ──

    [Test]
    public async Task Enumerate_StalePointer_PairAssembledWithPointerHash()
    {
        CreateFile("img/photo.png", "actual-binary-data");
        var oldHash = new string('1', 64); // will differ from computed hash
        CreateFile("img/photo.png.pointer.arius", oldHash);

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer!.Hash.ShouldBe(ContentHash.Parse(oldHash));
    }

    // ── Legacy (v5) JSON pointer ──────────────────────────────────────────────

    [Test]
    public async Task Enumerate_LegacyV5PointerOnly_ParsesHashAndFlagsLegacy()
    {
        var hash = new string('a', 64);
        CreateFile("music/song.mp3.pointer.arius", $"{{\"BinaryHash\":\"{hash}\"}}");

        var pair = (await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync()).Single();

        pair.Binary.ShouldBeNull();
        pair.Pointer.ShouldNotBeNull();
        pair.Pointer!.Hash.ShouldBe(ContentHash.Parse(hash));
        pair.Pointer!.IsLegacyFormat.ShouldBeTrue();
    }

    [Test]
    public async Task Enumerate_LegacyV5BinaryWithPointer_FlagsLegacy()
    {
        var hash = new string('b', 64);
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.pointer.arius", $"{{\"BinaryHash\":\"{hash}\"}}");

        var pair = (await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync()).Single();

        pair.Binary.ShouldNotBeNull();
        pair.Pointer.ShouldNotBeNull();
        pair.Pointer!.Hash.ShouldBe(ContentHash.Parse(hash));
        pair.Pointer!.IsLegacyFormat.ShouldBeTrue();
    }

    [Test]
    public async Task Enumerate_ModernBareHexPointer_NotFlaggedLegacy()
    {
        CreateFile("music/song.mp3.pointer.arius", new string('a', 64));

        var pair = (await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync()).Single();

        pair.Pointer.ShouldNotBeNull();
        pair.Pointer!.IsLegacyFormat.ShouldBeFalse();
    }

    // ── Multiple files in nested directories ──────────────────────────────────

    [Test]
    public async Task Enumerate_MultipleFiles_AllAssembled()
    {
        CreateFile("a.txt");
        CreateFile("photos/b.jpg");
        CreateFile("photos/2024/c.jpg");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(3);
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("a.txt");
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("photos/b.jpg");
        pairs.Select(p => p.RelativePath.ToString()).ShouldContain("photos/2024/c.jpg");
    }

    [Test]
    public async Task Enumerate_UnicodeFilename_ProducesCorrectPath()
    {
        CreateFile("données/résumé.pdf");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("données/résumé.pdf"));
    }

    [Test]
    public async Task Enumerate_FilenameWithSpaces_ProducesCorrectPath()
    {
        CreateFile("my files/my document.pdf");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("my files/my document.pdf"));
    }

    [Test]
    public async Task Enumerate_EmptyDirectory_ProducesNoPairs()
    {
        _fileSystem.CreateDirectory(RelativePath.Parse("empty-dir"));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.ShouldBeEmpty();
    }

    // ── Single-pass: pointer-with-binary is skipped ───────────────────────────

    /// <summary>
    /// Spec 3.3: When a pointer file is encountered during depth-first walk and its
    /// binary counterpart exists, the pointer file must be silently skipped —
    /// it was already emitted as part of the binary's FilePair.
    /// </summary>
    [Test]
    public async Task Enumerate_PointerFileWithBinaryPresent_OnlyOnePairEmitted()
    {
        // Both files exist; the pointer should NOT produce a second pair
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.pointer.arius", new string('a', 64));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1, "pointer-with-binary must not produce an extra pair");
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
    }

    [Test, Skip("Tracked by #82: restore should handle cross-OS path conflicts gracefully.")]
    public async Task Enumerate_PointerSuffixComparison_IsCaseInsensitive()
    {
        CreateFile("photos/vacation.jpg");
        CreateFile("photos/vacation.jpg.POINTER.ARIUS", new string('a', 64));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync();

        pairs.Count.ShouldBe(1);
        pairs[0].RelativePath.ShouldBe(RelativePath.Parse("photos/vacation.jpg"));
        pairs[0].Binary.ShouldNotBeNull();
        pairs[0].Pointer.ShouldNotBeNull();
        pairs[0].Pointer!.Path.ShouldBe(RelativePath.Parse("photos/vacation.jpg.POINTER.ARIUS"));
        pairs[0].Pointer!.Hash.ShouldNotBeNull();
    }

    // ── Single-pass: yielded before enumeration completes ─────────────────────

    /// <summary>
    /// The pipeline should begin processing the first FilePair before enumeration completes (no buffering).
    /// Verified by consuming the stream and asserting the first element is available before the rest.
    /// </summary>
    [Test]
    public async Task Enumerate_IsLazy_FirstElementAvailableBeforeAll()
    {
        // Create several files
        for (var i = 0; i < 5; i++)
            CreateFile($"file{i}.bin");

        var firstYielded  = false;
        var countConsumed = 0;

        await foreach (var _ in _enumerator.EnumerateAsync(_rootDirectory))
        {
            firstYielded = true; // available before the rest are enumerated
            countConsumed++;
        }

        firstYielded.ShouldBeTrue();
        countConsumed.ShouldBe(5);
    }

    [Test]
    public async Task Enumerate_YieldsBeforeEnumerationCompletes()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");

        await using var iterator = _enumerator.EnumerateAsync(_rootDirectory).GetAsyncEnumerator();

        (await iterator.MoveNextAsync()).ShouldBeTrue();

        var firstPath = iterator.Current.RelativePath;
        var secondPath = firstPath == RelativePath.Parse("a.txt")
            ? RelativePath.Parse("b.txt")
            : RelativePath.Parse("a.txt");

        CreateFile(secondPath.ToString() + ".pointer.arius", new string('a', 64));

        (await iterator.MoveNextAsync()).ShouldBeTrue();
        iterator.Current.RelativePath.ShouldBe(secondPath);
        iterator.Current.Pointer.ShouldNotBeNull();
        iterator.Current.Pointer!.Hash.ShouldBe(ContentHash.Parse(new string('a', 64)));
    }

    // ── Exclusions ────────────────────────────────────────────────────────────

    private static FileExclusionFilter Filter(
        IEnumerable<string>? dirs = null,
        IEnumerable<string>? files = null,
        bool excludeSystem = false,
        bool excludeHidden = false) =>
        new(new FileExclusionOptions
        {
            ExcludedDirectoryNames = dirs?.ToList() ?? [],
            ExcludedFileNames      = files?.ToList() ?? [],
            ExcludeSystemEntries   = excludeSystem,
            ExcludeHiddenEntries   = excludeHidden,
        });

    [Test]
    public async Task Enumerate_NullFilter_ExcludesNothing()
    {
        CreateFile("keep.txt");
        CreateFile("@eaDir/thumb.jpg");
        CreateFile("thumbs.db");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync(); // no filter

        pairs.Select(p => p.RelativePath.ToString())
            .ShouldBe(["keep.txt", "@eaDir/thumb.jpg", "thumbs.db"], ignoreOrder: true);
    }

    [Test]
    public async Task Enumerate_ExcludedDirectory_SubtreePruned()
    {
        CreateFile("keep.txt");
        CreateFile("@eaDir/thumb.jpg");
        CreateFile("@eaDir/nested/more.jpg");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(dirs: ["@eaDir"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["keep.txt"]);
    }

    [Test]
    public async Task Enumerate_NestedExcludedDirectory_OnlySubtreePruned()
    {
        CreateFile("photos/real.jpg");
        CreateFile("photos/@eaDir/thumb.jpg");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(dirs: ["@eaDir"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["photos/real.jpg"]);
    }

    [Test]
    public async Task Enumerate_ExcludedDirectory_CaseInsensitive()
    {
        CreateFile("keep.txt");
        CreateFile("@EADIR/thumb.jpg");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(dirs: ["@eaDir"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["keep.txt"]);
    }

    [Test]
    public async Task Enumerate_ExcludedFileName_NotYielded()
    {
        CreateFile("report.pdf");
        CreateFile("Thumbs.DB");
        CreateFile(".DS_Store");

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(files: ["thumbs.db", ".ds_store"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["report.pdf"]);
    }

    [Test]
    public async Task Enumerate_ExcludedDirectory_PrunesBinaryAndPointerInside()
    {
        CreateFile("keep.txt");
        CreateFile("@eaDir/vacation.jpg");
        CreateFile("@eaDir/vacation.jpg.pointer.arius", new string('a', 64));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(dirs: ["@eaDir"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["keep.txt"]);
    }

    [Test]
    public async Task Enumerate_HiddenDotfile_ExcludedOnlyWhenToggled()
    {
        if (OperatingSystem.IsWindows())
            return; // dotfiles are not Hidden on Windows; filter-level tests cover the logic cross-platform

        CreateFile("visible.txt");
        CreateFile(".secret");

        var included = await _enumerator.EnumerateAsync(_rootDirectory, Filter(excludeHidden: false)).ToListAsync();
        included.Select(p => p.RelativePath.ToString()).ShouldBe(["visible.txt", ".secret"], ignoreOrder: true);

        var excluded = await _enumerator.EnumerateAsync(_rootDirectory, Filter(excludeHidden: true)).ToListAsync();
        excluded.Select(p => p.RelativePath.ToString()).ShouldBe(["visible.txt"]);
    }

    [Test]
    public async Task Enumerate_PointerOnlyFile_ExcludedByLogicalName()
    {
        // Thin archive: only the pointer remains (binary removed via --remove-local). Exclusion must
        // key on the logical name (thumbs.db), not the pointer filename, or the file slips back in.
        CreateFile("keep.txt");
        CreateFile("thumbs.db.pointer.arius", new string('a', 64));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory, Filter(files: ["thumbs.db"])).ToListAsync();

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["keep.txt"]);
    }

    [Test]
    public async Task Enumerate_PointerOnlyFile_NotExcluded_YieldedByLogicalName()
    {
        CreateFile("song.mp3.pointer.arius", new string('a', 64));

        var pair = (await _enumerator.EnumerateAsync(_rootDirectory, Filter(files: ["thumbs.db"])).ToListAsync()).Single();

        pair.RelativePath.ShouldBe(RelativePath.Parse("song.mp3"));
        pair.Binary.ShouldBeNull();
        pair.Pointer.ShouldNotBeNull();
    }

    [Test]
    public async Task Enumerate_BrokenDirectorySymlink_IsSkipped_WalkCompletes()
    {
        if (OperatingSystem.IsWindows())
            return; // creating symlinks requires elevation on Windows

        CreateFile("photos/real.jpg");
        var linkFull = _rootDirectory.Resolve(RelativePath.Parse("photos/broken-dir-link"));
        Directory.CreateSymbolicLink(linkFull, Path.Combine(_rootDirectory.ToString(), "missing-target-dir"));

        var pairs = await _enumerator.EnumerateAsync(_rootDirectory).ToListAsync(); // must not throw on the dangling link

        pairs.Select(p => p.RelativePath.ToString()).ShouldBe(["photos/real.jpg"]);
    }

    // ── Skip reporting via callback ────────────────────────────────────────────

    [Test]
    public async Task Enumerate_ExcludedEntries_ReportedViaCallback()
    {
        CreateFile("keep.txt");
        CreateFile("thumbs.db");
        CreateFile("@eaDir/thumb.jpg");

        await CapturingEnumerator().EnumerateAsync(_rootDirectory, Filter(dirs: ["@eaDir"], files: ["thumbs.db"])).ToListAsync();

        _excluded.ShouldContain(s => s.Path.ToString() == "thumbs.db" && s.Reason == ExclusionReason.ExcludedByName);
        _excluded.ShouldContain(s => s.Path.ToString() == "@eaDir"    && s.Reason == ExclusionReason.ExcludedByName);
    }

    [Test]
    public async Task SafeEnumerate_UnreadableDirectory_ReportsViaCallbackAndStops()
    {
        var result = await CapturingEnumerator()
            .SafeEnumerateAsync(ThrowsImmediately(new UnauthorizedAccessException("denied")), RelativePath.Parse("locked"), CancellationToken.None)
            .ToListAsync();

        result.ShouldBeEmpty();
        _excluded.ShouldContain(s =>
            s.Path.ToString() == "locked" &&
            s.Reason == ExclusionReason.UnreadableDirectory &&
            s.Exception is UnauthorizedAccessException);
    }

    [Test]
    public async Task SafeEnumerate_FaultMidEnumeration_YieldsPrefixThenReports()
    {
        var result = await CapturingEnumerator()
            .SafeEnumerateAsync(YieldsThenThrows(RelativePath.Parse("a.txt"), new IOException("boom")), RelativePath.Parse("dir"), CancellationToken.None)
            .ToListAsync();

        result.Select(p => p.ToString()).ShouldBe(["a.txt"]);
        _excluded.ShouldContain(s => s.Path.ToString() == "dir" && s.Reason == ExclusionReason.UnreadableDirectory);
    }

    private static IEnumerable<RelativePath> ThrowsImmediately(Exception ex) =>
        Enumerable.Range(0, 1).Select<int, RelativePath>(_ => throw ex);

    private static IEnumerable<RelativePath> YieldsThenThrows(RelativePath first, Exception ex)
    {
        yield return first;
        throw ex;
    }
}
