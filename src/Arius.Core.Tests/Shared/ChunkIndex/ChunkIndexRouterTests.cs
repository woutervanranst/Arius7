using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexRouterTests
{
    // ── ResolveTarget: parent-wins walk ──────────────────────────────────────

    [Test]
    public void ResolveTarget_ParentAndChildExist_ParentWins()
    {
        // An interrupted split leaves parent + children coexisting; the parent is authoritative.
        var target = ChunkIndexRouter.ResolveTarget(Names("aa", "aa3"), Hash("aa3f"));

        target.ShouldBe(new ShardTarget(PathSegment.Parse("aa"), Exists: true));
    }

    [Test]
    public void ResolveTarget_OnlyChildExists_DescendsToChild()
    {
        var target = ChunkIndexRouter.ResolveTarget(Names("aa3"), Hash("aa3f"));

        target.ShouldBe(new ShardTarget(PathSegment.Parse("aa3"), Exists: true));
    }

    [Test]
    public void ResolveTarget_EmptySubtree_EmptyAtRootDepth()
    {
        var target = ChunkIndexRouter.ResolveTarget(Names(), Hash("aa3f"));

        target.ShouldBe(new ShardTarget(PathSegment.Parse("aa"), Exists: false));
    }

    [Test]
    public void ResolveTarget_SplitRootWithoutMatchingChild_EmptyAtChildDepth()
    {
        // Siblings exist, so "aa" was split; the hash's own slot has no blob → empty at depth 3.
        var target = ChunkIndexRouter.ResolveTarget(Names("aa0", "aa1"), Hash("aa5f"));

        target.ShouldBe(new ShardTarget(PathSegment.Parse("aa5"), Exists: false));
    }

    [Test]
    public void ResolveTarget_SkipLevelLayout_DescendsThroughAbsentIntermediate()
    {
        // Only a grandchild exists ("aa" and "aa3" absent): the walk descends through the absent
        // intermediate level and resolves the grandchild, or the terminal empty depth.
        var names = Names("aa3f");

        ChunkIndexRouter.ResolveTarget(names, Hash("aa3f")).ShouldBe(new ShardTarget(PathSegment.Parse("aa3f"), Exists: true));
        ChunkIndexRouter.ResolveTarget(names, Hash("aa31")).ShouldBe(new ShardTarget(PathSegment.Parse("aa31"), Exists: false));
        ChunkIndexRouter.ResolveTarget(names, Hash("aa7f")).ShouldBe(new ShardTarget(PathSegment.Parse("aa7"), Exists: false));
    }

    // ── ChildPrefixes ─────────────────────────────────────────────────────────

    [Test]
    public void ChildPrefixes_AppendsEachHexCharacter()
    {
        ChunkIndexRouter.ChildPrefixes(PathSegment.Parse("aa")).Select(p => p.ToString())
            .ShouldBe(["aa0", "aa1", "aa2", "aa3", "aa4", "aa5", "aa6", "aa7", "aa8", "aa9", "aaa", "aab", "aac", "aad", "aae", "aaf"]);
    }

    // ── GetHashRangeBounds ───────────────────────────────────────────────────

    [Test]
    public void GetHashRangeBounds_EvenPrefix_SpansFullRange()
    {
        var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(PathSegment.Parse("aa"));

        Convert.ToHexStringLower(lower).ShouldBe("aa" + new string('0', 62));
        Convert.ToHexStringLower(upper).ShouldBe("aa" + new string('f', 62));
    }

    [Test]
    public void GetHashRangeBounds_OddNibblePrefix_SpansHalfByteRange()
    {
        var (lower, upper) = ChunkIndexRouter.GetHashRangeBounds(PathSegment.Parse("aa3"));

        Convert.ToHexStringLower(lower).ShouldBe("aa3" + new string('0', 61));
        Convert.ToHexStringLower(upper).ShouldBe("aa3" + new string('f', 61));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlySet<string> Names(params string[] names) => names.ToHashSet(StringComparer.Ordinal);

    private static ContentHash Hash(string prefix) => ContentHash.Parse(prefix.PadRight(64, '9'));
}
