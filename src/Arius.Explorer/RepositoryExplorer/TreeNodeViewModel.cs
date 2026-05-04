using System;
using System.Collections.ObjectModel;
using Arius.Core.Shared.Paths;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arius.Explorer.RepositoryExplorer;

public partial class TreeNodeViewModel : ObservableObject
{
    private readonly RelativePath prefix;
    private readonly Action<TreeNodeViewModel>? onSelected;

    public RelativePath Prefix => prefix;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private ObservableCollection<TreeNodeViewModel> folders = [];

    [ObservableProperty]
    private ObservableCollection<FileItemViewModel> items = [];

    public TreeNodeViewModel(RelativePath prefix, Action<TreeNodeViewModel>? onSelected = null, bool showPlaceholder = true)
    {
        this.prefix = prefix;
        this.onSelected = onSelected;
        Name = prefix.Name?.ToString() ?? string.Empty;

        // Add placeholder child to show expansion chevron
        if (showPlaceholder)
        {
            folders = [new TreeNodeViewModel(RelativePath.Root, null, false) { Name = "Loading..." }];
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            IsExpanded = true; // Expand when selected
            onSelected?.Invoke(this);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            IsSelected = true; // Select when expanded
        }
    }
}
