using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Paths;
using Arius.Explorer.Infrastructure;
using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;

namespace Arius.Explorer.RepositoryExplorer;

public partial class RepositoryExplorerViewModel : ObservableObject
{
    public static Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowMessageBox { get; set; } =
        static (message, caption, button, image) => MessageBox.Show(message, caption, button, image);

    private readonly IApplicationSettings                 settings;
    private readonly IRecentRepositoryManager             recentRepositoryManager;
    private readonly IDialogService                       dialogService;
    private readonly IRepositorySession                   repositorySession;
    private readonly ILogger<RepositoryExplorerViewModel> logger;
    private CancellationTokenSource? nodeLoadCancellation;
    private CancellationTokenSource? hydrationLoadCancellation;

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
        Repository = repository;

        // Load repository data asynchronously
        if (repository != null)
        {
            await LoadRepositoryAsync();
            recentRepositoryManager.TouchOrAdd(repository);
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
            var rootNode = new TreeNodeViewModel(RelativePath.Root, OnNodeSelected)
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
        var cancellationTokenSource = new CancellationTokenSource();
        CancelNodeLoad();
        nodeLoadCancellation = cancellationTokenSource;
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            CancelHydrationLoad();

            if (Repository == null)
                return;

            if (repositorySession.Mediator is null)
                throw new InvalidOperationException("Repository session is not connected.");

            var query = new ListQuery(new ListQueryOptions
            {
                Prefix = node.Prefix,
                Recursive = false,
                LocalPath = Repository.LocalRoot,
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
                var results = repositorySession.Mediator.CreateStream(query, cancellationToken);

                await foreach (var result in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                        switch (result)
                        {
                            case RepositoryDirectoryEntry directory:
                            var childNode = new TreeNodeViewModel(directory.RelativePath, OnNodeSelected);
                            node.Folders.Add(childNode);

                            break;

                        case RepositoryFileEntry file:
                            var fileItem = new FileItemViewModel(file);

                            node.Items.Add(fileItem);
                            OnPropertyChanged(nameof(SelectedItemsText));

                            break;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Final count update (in case there were only directories)
                OnPropertyChanged(nameof(SelectedItemsText));
                _ = LoadHydrationStatusesAsync(node);
            }
            finally
            {
                if (ReferenceEquals(nodeLoadCancellation, cancellationTokenSource))
                {
                    nodeLoadCancellation = null;
                    IsLoading            = false;
                    ArchiveStatistics    = ""; // STATISTICS TODO
                }

                cancellationTokenSource.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Node content load was cancelled.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading node content");
            ShowMessageBox(
                $"Failed to load repository content: {e.Message}\n\nPlease check your repository options (Account Name, Account Key, Container Name, and Passphrase).",
                "Error Loading Repository",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task LoadHydrationStatusesAsync(TreeNodeViewModel node)
    {
        if (repositorySession.Mediator is null || node.Items.Count == 0)
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        hydrationLoadCancellation = cancellationTokenSource;
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            var cloudFiles = node.Items
                .Where(item => item.File.ExistsInCloud && item.File.ContentHash is not null)
                .Select(item => item.File)
                .ToList();

            if (cloudFiles.Count == 0)
                return;

            var lookup = node.Items.ToDictionary(
                item => item.File.RelativePath,
                item => item);

            await foreach (var status in repositorySession.Mediator.CreateStream(new ChunkHydrationStatusQuery(cloudFiles), cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (SelectedTreeNode != node)
                    return;

                if (lookup.TryGetValue(status.RelativePath, out var item))
                {
                    item.HydrationStatus = status.Status;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Hydration status load was cancelled for {Prefix}", node.Prefix);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lazily load hydration statuses for {Prefix}", node.Prefix);
        }
        finally
        {
            if (ReferenceEquals(hydrationLoadCancellation, cancellationTokenSource))
            {
                hydrationLoadCancellation = null;
            }
            cancellationTokenSource.Dispose();
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

        await EnsureHydrationStatusesAsync(SelectedFiles, CancellationToken.None);

        // Show confirmation dialog
        var msg = new StringBuilder();

        var itemsToHydrate = SelectedFiles.Where(item => item.HydrationStatus == ChunkHydrationStatus.NeedsRehydration);
        if (itemsToHydrate.Any())
            msg.AppendLine($"This will start hydration on {itemsToHydrate.Count()} item(s) ({itemsToHydrate.Sum(item => item.OriginalLength).Bytes().Humanize()}). This may incur a significant cost.");

        var itemsPending = SelectedFiles.Where(item => item.HydrationStatus == ChunkHydrationStatus.RehydrationPending);
        if (itemsPending.Any())
            msg.AppendLine($"{itemsPending.Count()} item(s) ({itemsPending.Sum(item => item.OriginalLength).Bytes().Humanize()}) are already rehydrating in the cloud.");

        var itemsToRestore = SelectedFiles.Where(item => item.HydrationStatus == ChunkHydrationStatus.Available || item.HydrationStatus == ChunkHydrationStatus.Unknown);
        msg.AppendLine($"This will download {itemsToRestore.Count()} item(s) ({itemsToRestore.Sum(item => item.OriginalLength).Bytes().Humanize()}).");
        msg.AppendLine();
        msg.AppendLine("Proceed?");

        if (ShowMessageBox(msg.ToString(), App.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
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
                    RootDirectory = Repository.LocalRoot,
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
            ShowMessageBox($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task EnsureHydrationStatusesAsync(IEnumerable<FileItemViewModel> items, CancellationToken cancellationToken)
    {
        if (repositorySession.Mediator is null)
            return;

        var unresolved = items
            .Where(item => item.HydrationStatus == ChunkHydrationStatus.Unknown && item.File.ExistsInCloud && item.File.ContentHash is not null)
            .Select(item => item.File)
            .ToList();

        if (unresolved.Count == 0)
            return;

        var lookup = items.ToDictionary(item => item.File.RelativePath, item => item);

        await foreach (var status in repositorySession.Mediator.CreateStream(new ChunkHydrationStatusQuery(unresolved), cancellationToken))
        {
            if (lookup.TryGetValue(status.RelativePath, out var item))
            {
                item.HydrationStatus = status.Status;
            }
        }
    }

    private void CancelHydrationLoad()
    {
        hydrationLoadCancellation?.Cancel();
        hydrationLoadCancellation?.Dispose();
        hydrationLoadCancellation = null;
    }

    private void CancelNodeLoad()
    {
        nodeLoadCancellation?.Cancel();
        nodeLoadCancellation?.Dispose();
        nodeLoadCancellation = null;
    }

}
