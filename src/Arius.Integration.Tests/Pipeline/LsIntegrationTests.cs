using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Integration tests for the ls command: various filters, snapshot version resolution.
/// Covers task 11.7.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class LsIntegrationTests(AzuriteFixture azurite)
{
    private static byte[] Rnd(int size)
    {
        var b = new byte[size]; Random.Shared.NextBytes(b); return b;
    }

    // ── 11.7a: ls returns all files in snapshot ───────────────────────────────

    [Test]
    public async Task Ls_NoFilters_ReturnsAllFiles()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile("photos/vacation.jpg",   Rnd(100));
        fix.WriteFile("photos/sunset.jpg",     Rnd(200));
        fix.WriteFile("docs/readme.txt",       Rnd(50));
        fix.WriteFile("root.txt",              Rnd(20));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        var result = await fix.LsAsync();
        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.Entries.Count.ShouldBe(4);

        var paths = result.Entries.Select(e => e.RelativePath).ToHashSet();
        paths.ShouldContain("photos/vacation.jpg");
        paths.ShouldContain("photos/sunset.jpg");
        paths.ShouldContain("docs/readme.txt");
        paths.ShouldContain("root.txt");
    }

    // ── 11.7b: prefix filter ──────────────────────────────────────────────────

    [Test]
    public async Task Ls_WithPrefixFilter_ReturnsOnlyMatchingSubtree()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile("photos/vacation.jpg",   Rnd(100));
        fix.WriteFile("photos/sunset.jpg",     Rnd(200));
        fix.WriteFile("docs/readme.txt",       Rnd(50));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var result = await fix.LsAsync(new Core.Ls.LsOptions { Prefix = "photos" });
        result.Success.ShouldBeTrue();
        result.Entries.Count.ShouldBe(2);
        result.Entries.All(e => e.RelativePath.StartsWith("photos")).ShouldBeTrue();
    }

    // ── 11.7c: filename substring filter ──────────────────────────────────────

    [Test]
    public async Task Ls_WithFilenameFilter_ReturnsCaseInsensitiveMatches()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile("photos/VACATION.jpg",   Rnd(100));
        fix.WriteFile("photos/sunset.jpg",     Rnd(200));
        fix.WriteFile("docs/readme.txt",       Rnd(50));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var result = await fix.LsAsync(new Core.Ls.LsOptions { Filter = "vacation" });
        result.Success.ShouldBeTrue();
        result.Entries.Count.ShouldBe(1);
        result.Entries[0].RelativePath.ShouldContain("VACATION");
    }

    // ── 11.7d: size field populated from chunk index ───────────────────────────

    [Test]
    public async Task Ls_Entries_HaveOriginalSizeFromIndex()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = Rnd(1234);
        fix.WriteFile("file.bin", content);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var result = await fix.LsAsync();
        result.Success.ShouldBeTrue();
        result.Entries.Count.ShouldBe(1);
        result.Entries[0].OriginalSize.ShouldBe(1234);
    }

    // ── 11.7e: ls with snapshot version ──────────────────────────────────────

    [Test]
    public async Task Ls_WithVersion_ReturnsCorrectSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile("v1-only.txt", Rnd(50));
        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100);

        fix.WriteFile("v2-added.txt", Rnd(50));
        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue();

        // ls latest → both files
        var lsLatest = await fix.LsAsync();
        lsLatest.Entries.Count.ShouldBe(2);

        // ls version 1 → only v1-only
        var lsV1 = await fix.LsAsync(new Core.Ls.LsOptions { Version = snapshot1 });
        lsV1.Success.ShouldBeTrue();
        lsV1.Entries.Count.ShouldBe(1);
        lsV1.Entries[0].RelativePath.ShouldBe("v1-only.txt");
    }
}
