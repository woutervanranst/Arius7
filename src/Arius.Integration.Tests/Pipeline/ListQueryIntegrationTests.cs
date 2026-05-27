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

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/vacation.jpg"), Rnd(100), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/sunset.jpg"), Rnd(200), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("docs/readme.txt"), Rnd(50), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("root.txt"), Rnd(20), CancellationToken.None);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        var entries = await fix.ListAsync();
        entries.Count.ShouldBe(4);

        var paths = entries.Select(e => e.RelativePath).ToHashSet();
        paths.ShouldContain(RelativePath.Parse("photos/vacation.jpg"));
        paths.ShouldContain(RelativePath.Parse("photos/sunset.jpg"));
        paths.ShouldContain(RelativePath.Parse("docs/readme.txt"));
        paths.ShouldContain(RelativePath.Parse("root.txt"));
    }

    // ── 11.7b: prefix filter ──────────────────────────────────────────────────

    [Test]
    public async Task ListQuery_WithPrefixFilter_ReturnsOnlyMatchingSubtree()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/vacation.jpg"), Rnd(100), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/sunset.jpg"), Rnd(200), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("docs/readme.txt"), Rnd(50), CancellationToken.None);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue();

        var entries = await fix.ListAsync(new ListQueryOptions { Prefix = RelativePath.Parse("photos") });
        entries.Count.ShouldBe(2);
        entries.All(e => e.RelativePath.StartsWith(RelativePath.Parse("photos"))).ShouldBeTrue();
    }

    // ── 11.7c: filename substring filter ──────────────────────────────────────

    [Test]
    public async Task ListQuery_WithFilenameFilter_ReturnsCaseInsensitiveMatches()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/VACATION.jpg"), Rnd(100), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("photos/sunset.jpg"), Rnd(200), CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("docs/readme.txt"), Rnd(50), CancellationToken.None);

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
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("file.bin"), content, CancellationToken.None);

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

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("v1-only.txt"), Rnd(50), CancellationToken.None);
        var r1 = await fix.ArchiveAsync();
        r1.Success.ShouldBeTrue();
        var snapshot1 = r1.SnapshotTime.ToString("yyyy-MM-ddTHHmmss");

        await Task.Delay(1100);

        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("v2-added.txt"), Rnd(50), CancellationToken.None);
        var r2 = await fix.ArchiveAsync();
        r2.Success.ShouldBeTrue();

        // ls latest → both files
        var listLatest = await fix.ListAsync();
        listLatest.Count.ShouldBe(2);

        // ls version 1 → only v1-only
        var listV1 = await fix.ListAsync(new ListQueryOptions { Version = snapshot1 });
        listV1.Count.ShouldBe(1);
        listV1[0].RelativePath.ShouldBe(RelativePath.Parse("v1-only.txt"));
    }
}
