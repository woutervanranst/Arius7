using Arius.Core.Features.ListQuery;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Integration tests for the list query: various filters, snapshot version resolution.
/// Covers task 11.7.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ListQueryIntegrationTests(AzuriteFixture azurite)
{
    private static byte[] Rnd(int size)
    {
        var b = new byte[size]; Random.Shared.NextBytes(b); return b;
    }

    // ── 11.7a: list query returns all files in snapshot ─────────────────────────

    [Test]
    public async Task ListQuery_NoFilters_ReturnsAllFiles()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("photos/vacation.jpg"), Rnd(100));
        fix.WriteFile(PathOf("photos/sunset.jpg"),   Rnd(200));
        fix.WriteFile(PathOf("docs/readme.txt"),     Rnd(50));
        fix.WriteFile(PathOf("root.txt"),            Rnd(20));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        var entries = await fix.ListAsync();
        entries.Count.ShouldBe(4);

        var paths = entries.Select(e => e.RelativePath).ToHashSet();
        paths.ShouldContain(PathOf("photos/vacation.jpg"));
        paths.ShouldContain(PathOf("photos/sunset.jpg"));
        paths.ShouldContain(PathOf("docs/readme.txt"));
        paths.ShouldContain(PathOf("root.txt"));
    }

    // ── 11.7b: prefix filter ──────────────────────────────────────────────────

    [Test]
    public async Task ListQuery_WithPrefixFilter_ReturnsOnlyMatchingSubtree()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("photos/vacation.jpg"), Rnd(100));
        fix.WriteFile(PathOf("photos/sunset.jpg"),   Rnd(200));
        fix.WriteFile(PathOf("docs/readme.txt"),     Rnd(50));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var entries = await fix.ListAsync(new ListQueryOptions { Prefix = PathOf("photos") });
        entries.Count.ShouldBe(2);
        entries.All(e => e.RelativePath.StartsWith(PathOf("photos"))).ShouldBeTrue();
    }

    // ── 11.7c: filename substring filter ──────────────────────────────────────

    [Test]
    public async Task ListQuery_WithFilenameFilter_ReturnsCaseInsensitiveMatches()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("photos/VACATION.jpg"), Rnd(100));
        fix.WriteFile(PathOf("photos/sunset.jpg"),   Rnd(200));
        fix.WriteFile(PathOf("docs/readme.txt"),     Rnd(50));

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var entries = await fix.ListAsync(new ListQueryOptions { Filter = "vacation" });
        entries.Count.ShouldBe(1);
        entries[0].RelativePath.ToString().ShouldContain("VACATION");
    }

    // ── 11.7d: size field populated from chunk index ───────────────────────────

    [Test]
    public async Task ListQuery_Entries_HaveOriginalSizeFromIndex()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        var content = Rnd(1234);
        fix.WriteFile(PathOf("file.bin"), content);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var entries = await fix.ListAsync();
        entries.Count.ShouldBe(1);
        entries[0].OriginalSize.ShouldBe(1234);
    }

    // ── 11.7e: list query with snapshot version ────────────────────────────────

    [Test]
    public async Task ListQuery_WithVersion_ReturnsCorrectSnapshot()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        fix.WriteFile(PathOf("v1-only.txt"), Rnd(50));
        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100);

        fix.WriteFile(PathOf("v2-added.txt"), Rnd(50));
        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue();

        // ls latest → both files
        var listLatest = await fix.ListAsync();
        listLatest.Count.ShouldBe(2);

        // ls version 1 → only v1-only
        var listV1 = await fix.ListAsync(new ListQueryOptions { Version = snapshot1 });
        listV1.Count.ShouldBe(1);
        listV1[0].RelativePath.ShouldBe(PathOf("v1-only.txt"));
    }
}
