using Arius.Explorer.RepositoryExplorer;

namespace Arius.Explorer.Tests.RepositoryExplorer;

public class TreeNodeViewModelTests
{
    [Test]
    public void Constructor_WithPlaceholder_AddsLoadingChild()
    {
        var node = new TreeNodeViewModel("/root/");

        node.Prefix.ShouldBe("/root/");
        node.Folders.Count.ShouldBe(1);
        node.Folders[0].Name.ShouldBe("Loading...");
    }

    [Test]
    public void IsSelected_WhenSetToTrue_ExpandsAndInvokesCallback()
    {
        TreeNodeViewModel selectedNode = null!;
        var node = new TreeNodeViewModel("/root/", onSelected: selected => selectedNode = selected, showPlaceholder: false);

        node.IsSelected = true;

        node.IsExpanded.ShouldBeTrue();
        selectedNode.ShouldBe(node);
    }

    [Test]
    public void IsExpanded_WhenSetToTrue_SelectsNode()
    {
        var node = new TreeNodeViewModel("/root/", showPlaceholder: false);

        node.IsExpanded = true;

        node.IsSelected.ShouldBeTrue();
    }
}
