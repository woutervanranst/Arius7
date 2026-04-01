using Arius.Core.Features.List;
using Arius.Core.Features.Restore;
using Arius.Explorer.Infrastructure;
using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Arius.Explorer.RepositoryExplorer;

public partial class RepositoryExplorerViewModel : ObservableObject
{
    private readonly IApplicationSettings                 settings;
    private readonly IRecentRepositoryManager             recentRepositoryManager;
    private readonly IDialogService                       dialogService;
    private readonly IRepositorySession                   repositorySession;
    private readonly ILogger<RepositoryExplorerViewModel> logger;

    // -- INITIALIZATION & GENERAL WINDOW

    public RepositoryExplorerViewModel(IApplicationSettings settings, IRecentRepositoryManager recentRepositoryManager, IDialogService dialogService, IRepositorySession repositorySession, ILogger<RepositoryExplorerViewModel> logger)
    {
        this.settings                = settings;
        this.recentRepositoryManager = recentRepositoryManager;
        this.dialogService           = dialogService;
        this.repositorySession       = repositorySession;
        this.logger                  = logger;

        // Load recent repositories from settings
        RecentRepositories = settings.RecentRepositories;

        // Check for most recent repository and auto-open if exists
        var mostRecent = recentRepositoryManager.GetMostRecent();
        if (mostRecent != null)
        {
            Repository = mostRecent; // this will trigger OnRepositoryChanged for UI updates
            _ = LoadRepositoryAsync(); // fire-and-forget for initial load
        }
    }

    [RelayCommand] // triggered by View's Loaded event
    private async Task ViewLoadedAsync()
    {
        // If the Explorer window is shown but no Repository is selected, open the ChooseRepository window
        if (Repository == null)
            await OpenChooseRepositoryDialogAsync();
    }

    [ObservableProperty]
    private string windowName = "Arius Explorer";

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string archiveStatistics = "";
    

    // MENUS

    //      File > Open...
    [RelayCommand] 
    private async Task OpenChooseRepositoryDialogAsync(CancellationToken cancellationToken = default)
    {
        // Show dialog and handle result
        var openedRepository = dialogService.ShowChooseRepositoryDialog(Repository);
        if (openedRepository != null)
        {
            await OpenRepositoryAsync(openedRepository, cancellationToken);
        }
    }

    //      File > Recent > [list]
    [ObservableProperty]
    private ObservableCollection<RepositoryOptions> recentRepositories = [];

    [RelayCommand]
    private async Task OpenRepositoryAsync(RepositoryOptions repository, CancellationToken cancellationToken = default)
    {
        // Use the new service to update recent repositories
        recentRepositoryManager.TouchOrAdd(repository);

        Repository = repository;

        // Load repository data asynchronously
        if (repository != null)
        {
            await LoadRepositoryAsync();
        }
    }


    // -- REPOSITORY

    [ObservableProperty]
    private RepositoryOptions? repository;

    private async Task LoadRepositoryAsync()
    {
        if (Repository == null)
        {
            WindowName        = $"{App.Name} - No Repository";
            RootNode          = [];
            SelectedTreeNode  = null;
            ArchiveStatistics = "";
            OnPropertyChanged(nameof(SelectedItemsText));
        }
        else
        {
            if (!Equals(repositorySession.Repository, Repository))
            {
                await repositorySession.ConnectAsync(Repository);
            }

            WindowName = $"{App.Name}: {Repository}";

            // NOTE no loading indicators here bc this is super quick; the actual loading happens in LoadNodeContentAsync

            // Create root node
            var rootNode = new TreeNodeViewModel(string.Empty, OnNodeSelected)
            {
                Name       = "Root",
                IsSelected = true,
                IsExpanded = true
            };

            RootNode         = [rootNode];
            SelectedTreeNode = rootNode;

            OnPropertyChanged(nameof(SelectedItemsText));
        }
    }

    private async void OnNodeSelected(TreeNodeViewModel selectedNode)
    {
        // Clear selection when switching nodes
        SelectedFiles.Clear();

        // Load the content for the selected node
        await LoadNodeContentAsync(selectedNode);
    }

    private async Task LoadNodeContentAsync(TreeNodeViewModel node)
    {
        try
        {
            if (Repository == null)
                return;

            if (repositorySession.Mediator is null)
                throw new InvalidOperationException("Repository session is not connected.");

            var query = new ListRepositoryEntriesCommand(new ListRepositoryEntriesCommandOptions
            {
                Prefix = string.IsNullOrWhiteSpace(node.Prefix) ? null : node.Prefix,
                Recursive = false,
                LocalPath = Repository.LocalDirectoryPath,
            });

            // Initialize collections for streaming updates
            node.Folders = [];
            node.Items   = [];

            // Update the selected tree node reference for ListView binding immediately
            SelectedTreeNode  = node;
            IsLoading         = true;
            ArchiveStatistics = "Loading...";

            try
            {
                var results = repositorySession.Mediator.CreateStream(query);

                await foreach (var result in results)
                {
                    switch (result)
                    {
                        case RepositoryDirectoryEntry directory:
                            var dirName = ExtractDirectoryName(directory.RelativePath);
                            var childNode = new TreeNodeViewModel(directory.RelativePath, OnNodeSelected)
                            {
                                Name = dirName
                            };

                            node.Folders.Add(childNode);

                            break;

                        case RepositoryFileEntry file:
                            var fileItem = new FileItemViewModel(file);

                            node.Items.Add(fileItem);
                            OnPropertyChanged(nameof(SelectedItemsText));

                            break;
                    }
                }

                // Final count update (in case there were only directories)
                OnPropertyChanged(nameof(SelectedItemsText));
            }
            finally
            {
                IsLoading         = false;
                ArchiveStatistics = ""; // STATISTICS TODO
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading node content");
            MessageBox.Show(
                $"Failed to load repository content: {e.Message}\n\nPlease check your repository options (Account Name, Account Key, Container Name, and Passphrase).",
                "Error Loading Repository",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    //      About

    [RelayCommand]
    private void About()
    {
        var explorerClickOnceVersion = Environment.GetEnvironmentVariable("ClickOnce_CurrentVersion") ?? "unknown"; // https://stackoverflow.com/a/75263211/1582323  //System.Deployment. System.Reflection.Assembly.GetEntryAssembly().GetName().Version; doesnt work
        var explorerVersion          = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "unknown";
        var x                        = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        var coreVersion              = typeof(Arius.Core.AssemblyMarker).Assembly.GetName().Version;

        MessageBox.Show($"""
                         Arius Explorer v{explorerVersion}, ClickOnce v{explorerClickOnceVersion}, Assembly v{x}
                         Arius Core v{coreVersion}
                         """, App.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }


    // TREEVIEW

    [ObservableProperty]
    private ObservableCollection<TreeNodeViewModel> rootNode = [];

    [ObservableProperty]
    private TreeNodeViewModel? selectedTreeNode;

    
    // LISTVIEW

    [ObservableProperty]
    private ObservableCollection<FileItemViewModel> selectedFiles = [];

    [RelayCommand]
    private void ItemSelectionChanged(FileItemViewModel item)
    {
        if (item.IsSelected)
        {
            if (!SelectedFiles.Contains(item))
            {
                SelectedFiles.Add(item);
            }
        }
        else
        {
            SelectedFiles.Remove(item);
        }
        OnPropertyChanged(nameof(SelectedItemsText));
    }

    public string SelectedItemsText
    {
        get
        {
            var selectedCount = SelectedFiles.Count;
            var totalCount    = SelectedTreeNode?.Items.Count ?? 0;
            var totalSize     = SelectedTreeNode?.Items.Sum(item => item.OriginalLength) ?? 0;

            if (selectedCount == 0)
            {
                if (totalCount == 0)
                {
                    return "0 item(s)";
                }
                else
                {

                    return totalCount > 0 ? $"{totalCount} item(s), {totalSize.Bytes().Humanize()}" : "";
                }
            }
            else
            {
                var totalSelectedSize = SelectedFiles.Sum(item => item.OriginalLength);

                return $"{selectedCount} of {totalCount} item(s) selected, {totalSelectedSize.Bytes().Humanize()} of {totalSize.Bytes().Humanize()}";
            }
        }
    }

    // RESTORE

    [RelayCommand]
    private async Task RestoreAsync()
    {
        // Validate prerequisites
        if (Repository == null || !SelectedFiles.Any())
            return;

        // Show confirmation dialog
        var msg = new StringBuilder();

        var itemsToHydrate = SelectedFiles.Where(item => item.File.Hydrated == false);
        if (itemsToHydrate.Any())
            msg.AppendLine($"This will start hydration on {itemsToHydrate.Count()} item(s) ({itemsToHydrate.Sum(item => item.OriginalLength).Bytes().Humanize()}). This may incur a significant cost.");

        var itemsToRestore = SelectedFiles.Where(item => item.File.Hydrated != false);
        msg.AppendLine($"This will download {itemsToRestore.Count()} item(s) ({itemsToRestore.Sum(item => item.OriginalLength).Bytes().Humanize()}).");
        msg.AppendLine();
        msg.AppendLine("Proceed?");

        if (MessageBox.Show(msg.ToString(), App.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
            return;

        if (repositorySession.Mediator is null)
            return;

        // Execute the restore
        try
        {
            IsLoading = true;

            foreach (var selectedFile in SelectedFiles)
            {
                var command = new RestoreCommand(new RestoreOptions
                {
                    RootDirectory = Repository.LocalDirectoryPath,
                    TargetPath = selectedFile.File.RelativePath,
                    Overwrite = true,
                    NoPointers = false,
                });

                var result = await repositorySession.Mediator.Send(command);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.ErrorMessage ?? $"Restore failed for {selectedFile.File.RelativePath}.");
                }
            }
            
            SelectedFiles.Clear();

            // Refresh the view after restore
            if (SelectedTreeNode != null)
                await LoadNodeContentAsync(SelectedTreeNode);
        }
        catch (Exception ex)
        {
            // Handle error (optionally show message)
            MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string ExtractDirectoryName(string relativeName) // TODO move this logic to the TreeNodeViewModel, just like FileItemViewModel
    {
        // Extract directory name from path like "/folder1/folder2/" -> "folder2"
        var trimmed = relativeName.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }
}
