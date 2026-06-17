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

    // ── PartitionIntoLeaves ──────────────────────────────────────────────────

    [Test]
    public void PartitionIntoLeaves_SingleLevel_ProducesOnlyNonEmptyChildren()
    {
        var entries = new[] { Entry("aa01"), Entry("aa02"), Entry("aa5f") };

        var leaves = ChunkIndexRouter.PartitionIntoLeaves(PathSegment.Parse("aa"), entries, maxEntryCount: 2);

        leaves.Select(l => l.Prefix.ToString()).ShouldBe(["aa0", "aa5"]);
        leaves.Single(l => l.Prefix.ToString() == "aa0").Entries.Select(e => e.ContentHash).ShouldBe([entries[0].ContentHash, entries[1].ContentHash], ignoreOrder: true);
        leaves.Single(l => l.Prefix.ToString() == "aa5").Entries.Single().ContentHash.ShouldBe(entries[2].ContentHash);
    }

    [Test]
    public void PartitionIntoLeaves_ChildStillOverThreshold_RecursesDeeper()
    {
        var entries = new[] { Entry("aa30"), Entry("aa31"), Entry("aa3f"), Entry("aa70") };

        var leaves = ChunkIndexRouter.PartitionIntoLeaves(PathSegment.Parse("aa"), entries, maxEntryCount: 2);

        leaves.Select(l => l.Prefix.ToString()).ShouldBe(["aa30", "aa31", "aa3f", "aa7"]);
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

    private static ShardEntry Entry(string prefix) => new(Hash(prefix), FakeChunkHash('e'), 10, 5, BlobTier.Cool);
}
