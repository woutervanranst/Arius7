using Arius.Core.Shared.Paths;
using Arius.Explorer.RepositoryExplorer;

namespace Arius.Explorer.Tests.RepositoryExplorer;

public class TreeNodeViewModelTests
{
    [Test]
    public void Constructor_WithPlaceholder_AddsLoadingChild()
    {
        var node = new TreeNodeViewModel(PathOf("root"));

        node.Prefix.ShouldBe(PathOf("root"));
        node.Name.ShouldBe("root");
        node.Folders.Count.ShouldBe(1);
        node.Folders[0].Name.ShouldBe("Loading...");
    }

    [Test]
    public void Constructor_WhenPrefixIsNested_UsesLastSegmentAsName()
    {
        var node = new TreeNodeViewModel(PathOf("folder1/folder2"), showPlaceholder: false);

        node.Name.ShouldBe("folder2");
    }

    [Test]
    public void IsSelected_WhenSetToTrue_ExpandsAndInvokesCallback()
    {
        TreeNodeViewModel selectedNode = null!;
        var node = new TreeNodeViewModel(PathOf("root"), onSelected: selected => selectedNode = selected, showPlaceholder: false);

        node.IsSelected = true;

        node.IsExpanded.ShouldBeTrue();
        selectedNode.ShouldBe(node);
    }

    [Test]
    public void IsExpanded_WhenSetToTrue_SelectsNode()
    {
        var node = new TreeNodeViewModel(PathOf("root"), showPlaceholder: false);

        node.IsExpanded = true;

        node.IsSelected.ShouldBeTrue();
    }
}
