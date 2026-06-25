using Arius.Core.Features.ArchiveCommand;
using Microsoft.Extensions.Configuration;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class FileExclusionFilterTests
{
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

    // ── Name-based exclusion ──────────────────────────────────────────────────

    [Test]
    public void ExcludesDirectoryByName_CaseInsensitive()
    {
        var filter = Filter(dirs: ["@eaDir"]);

        filter.ShouldExcludeDirectory(PathSegment.Parse("@eaDir"), default).ShouldBeTrue();
        filter.ShouldExcludeDirectory(PathSegment.Parse("@EADIR"), default).ShouldBeTrue();
        filter.ShouldExcludeDirectory(PathSegment.Parse("photos"), default).ShouldBeFalse();
    }

    [Test]
    public void ExcludesFileByName_CaseInsensitive()
    {
        var filter = Filter(files: ["thumbs.db", ".ds_store"]);

        filter.ShouldExcludeFile(PathSegment.Parse("thumbs.db"), default).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse("Thumbs.DB"), default).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse(".DS_Store"), default).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse("report.pdf"), default).ShouldBeFalse();
    }

    // ── Attribute-based exclusion (synthetic attributes → platform-independent) ──

    [Test]
    public void ExcludesSystemEntries_WhenEnabled()
    {
        var filter = Filter(excludeSystem: true);

        filter.ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.System).ShouldBeTrue();
        filter.ShouldExcludeDirectory(PathSegment.Parse("d"), FileAttributes.System).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.Normal).ShouldBeFalse();
    }

    [Test]
    public void IgnoresSystemAttribute_WhenDisabled()
    {
        var filter = Filter(excludeSystem: false);

        filter.ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.System).ShouldBeFalse();
        filter.ShouldExcludeDirectory(PathSegment.Parse("d"), FileAttributes.System).ShouldBeFalse();
    }

    [Test]
    public void ExcludesHiddenEntries_OnlyWhenEnabled()
    {
        Filter(excludeHidden: false).ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.Hidden).ShouldBeFalse();
        Filter(excludeHidden: true).ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.Hidden).ShouldBeTrue();
        Filter(excludeHidden: true).ShouldExcludeDirectory(PathSegment.Parse("d"), FileAttributes.Hidden).ShouldBeTrue();
    }

    [Test]
    public void RequiresAttributes_ReflectsToggles()
    {
        Filter().RequiresAttributes.ShouldBeFalse();
        Filter(excludeSystem: true).RequiresAttributes.ShouldBeTrue();
        Filter(excludeHidden: true).RequiresAttributes.ShouldBeTrue();
    }

    // ── None ──────────────────────────────────────────────────────────────────

    [Test]
    public void None_ExcludesNothing()
    {
        FileExclusionFilter.None.ShouldExcludeFile(PathSegment.Parse("thumbs.db"), FileAttributes.System | FileAttributes.Hidden).ShouldBeFalse();
        FileExclusionFilter.None.ShouldExcludeDirectory(PathSegment.Parse("@eaDir"), FileAttributes.System | FileAttributes.Hidden).ShouldBeFalse();
        FileExclusionFilter.None.RequiresAttributes.ShouldBeFalse();
    }

    // ── Embedded central defaults ─────────────────────────────────────────────

    [Test]
    public void EmbeddedDefaults_BindToExpectedValues()
    {
        var options = FileExclusionOptions.EmbeddedDefaultConfiguration()
            .GetSection(FileExclusionOptions.SectionName)
            .Get<FileExclusionOptions>();

        options.ShouldNotBeNull();
        options!.ExcludedDirectoryNames.ShouldBe(["@eaDir", "eaDir", "SynoResource"], ignoreOrder: true);
        options.ExcludedFileNames.ShouldBe(["autorun.ini", "thumbs.db", ".ds_store"], ignoreOrder: true);
        options.ExcludeSystemEntries.ShouldBeTrue();
        options.ExcludeHiddenEntries.ShouldBeFalse();
    }
}
