using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkStorage;
using Arius.Explorer.Infrastructure;
using Arius.Explorer.RepositoryExplorer;
using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Services;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Explorer.Tests.RepositoryExplorer;

[NotInParallel("RepositoryExplorerViewModelTests")]
public class RepositoryExplorerViewModelTests
{
    private static readonly ContentHash ContentHashA = FakeContentHash('a');
    private static readonly ContentHash ContentHashB = FakeContentHash('b');
    private static readonly ContentHash ContentHashC = FakeContentHash('c');
    private static readonly FileTreeHash TreeHashD = FakeFileTreeHash('d');

    private Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> originalShowMessageBox = null!;

    [Before(Test)]
    public void ResetMessageBox()
    {
        originalShowMessageBox = RepositoryExplorerViewModel.ShowMessageBox;
        RepositoryExplorerViewModel.ShowMessageBox = static (_, _, _, _) => MessageBoxResult.OK;
    }

    [After(Test)]
    public void RestoreMessageBox()
    {
        RepositoryExplorerViewModel.ShowMessageBox = originalShowMessageBox;
    }

    [Test]
    public void Constructor_WhenNoRecentRepository_StartsEmpty()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        using var repositorySession = new FakeRepositorySession();
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger);

        viewModel.RecentRepositories.ShouldBe(settings.RecentRepositories);
        viewModel.Repository.ShouldBeNull();
        viewModel.SelectedItemsText.ShouldBe("0 item(s)");
    }

    [Test]
    public async Task Constructor_WhenRecentRepositoryExists_LoadsRepositoryTree()
    {
        var repository = CreateRepository();
        var settings = new FakeApplicationSettings();
        settings.RecentRepositories.Add(repository);
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        recentRepositoryManager.GetMostRecent().Returns(repository);

        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable<RepositoryEntry>(
                new RepositoryDirectoryEntry("/folder1/folder2/", TreeHashD, true, true),
                new RepositoryFileEntry("/folder1/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, null),
                new RepositoryFileEntry("/folder1/file-b.txt", null, 2048, null, null, false, true, false, true, null)));
        mediator.CreateStream(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(
                new ChunkHydrationStatusResult("/folder1/file-a.txt", ContentHashA, ChunkHydrationStatus.Available)));

        var dialogService = Substitute.For<IDialogService>();
        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger);

        await WaitForAsync(viewModel, () =>
            viewModel.RootNode.Count == 1 &&
            viewModel.SelectedTreeNode?.Items.Count == 2 &&
            viewModel.SelectedTreeNode.Items[0].HydrationStatus == ChunkHydrationStatus.Available);

        repositorySession.ConnectCalls.ShouldBe(1);
        viewModel.Repository.ShouldBe(repository);
        viewModel.RootNode.Count.ShouldBe(1);
        viewModel.SelectedTreeNode.ShouldNotBeNull();
        viewModel.SelectedTreeNode.Name.ShouldBe("Root");
        viewModel.SelectedTreeNode.Folders.Count.ShouldBe(1);
        viewModel.SelectedTreeNode.Folders[0].Name.ShouldBe("folder2");
        viewModel.SelectedTreeNode.Items[0].HydrationStatus.ShouldBe(ChunkHydrationStatus.Available);
        viewModel.SelectedItemsText.ShouldContain("2 item(s)");
    }

    [Test]
    public async Task ViewLoadedCommand_WhenRepositoryMissing_OpensDialog()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        using var repositorySession = new FakeRepositorySession();
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger);

        await viewModel.ViewLoadedCommand.ExecuteAsync(null);

        dialogService.Received(1).ShowChooseRepositoryDialog(null);
    }

    [Test]
    public async Task ViewLoadedCommand_WhenRepositoryAlreadySet_DoesNotOpenDialog()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        using var repositorySession = new FakeRepositorySession();
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger)
        {
            Repository = CreateRepository()
        };

        await viewModel.ViewLoadedCommand.ExecuteAsync(null);

        dialogService.DidNotReceive().ShowChooseRepositoryDialog(Arg.Any<RepositoryOptions>());
    }

    [Test]
    public async Task OpenChooseRepositoryDialogCommand_WhenRepositorySelected_LoadsRepositoryAndTracksRecent()
    {
        var repository = CreateRepository();
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        dialogService.ShowChooseRepositoryDialog(null).Returns(repository);

        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>()).Returns(_ => EmptyAsyncEnumerable<RepositoryEntry>());
        mediator.CreateStream(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>()).Returns(_ => EmptyAsyncEnumerable<ChunkHydrationStatusResult>());

        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger);

        await viewModel.OpenChooseRepositoryDialogCommand.ExecuteAsync(null);
        await WaitForAsync(viewModel, () => viewModel.RootNode.Count == 1 && viewModel.Repository != null);

        viewModel.Repository.ShouldBe(repository);
        repositorySession.ConnectCalls.ShouldBe(1);
        repositorySession.Repository.ShouldBe(repository);
        recentRepositoryManager.Received(1).TouchOrAdd(repository);
        dialogService.Received(1).ShowChooseRepositoryDialog(null);
    }

    [Test]
    public void ItemSelectionChangedCommand_UpdatesSelectedFilesAndSummary()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        using var repositorySession = new FakeRepositorySession();
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger);
        var fileA = new FileItemViewModel(new RepositoryFileEntry("/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, true));
        var fileB = new FileItemViewModel(new RepositoryFileEntry("/file-b.txt", ContentHashB, 2048, null, null, true, true, true, true, true));
        var selectedTreeNode = new TreeNodeViewModel("/", showPlaceholder: false)
        {
            Items = new ObservableCollection<FileItemViewModel> { fileA, fileB }
        };

        viewModel.SelectedTreeNode = selectedTreeNode;

        fileA.IsSelected = true;
        viewModel.ItemSelectionChangedCommand.Execute(fileA);

        viewModel.SelectedFiles.ShouldContain(fileA);
        viewModel.SelectedItemsText.ShouldContain("1 of 2 item(s) selected");

        fileA.IsSelected = false;
        viewModel.ItemSelectionChangedCommand.Execute(fileA);

        viewModel.SelectedFiles.ShouldNotContain(fileA);
        viewModel.SelectedItemsText.ShouldContain("2 item(s)");
    }

    [Test]
    public async Task RestoreCommand_WhenNothingIsSelected_ReturnsWithoutCallingMediator()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        var mediator = Substitute.For<IMediator>();
        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();

        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger)
        {
            Repository = CreateRepository()
        };

        await viewModel.RestoreCommand.ExecuteAsync(null);

        await mediator.DidNotReceiveWithAnyArgs().Send(default!);
    }

    [Test]
    public async Task RestoreCommand_WhenUserDeclinesConfirmation_DoesNotSendRestoreCommands()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable<ChunkHydrationStatusResult>());

        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();
        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger)
        {
            Repository = CreateRepository(),
            SelectedTreeNode = new TreeNodeViewModel("/", showPlaceholder: false)
        };

        viewModel.SelectedFiles.Add(new FileItemViewModel(new RepositoryFileEntry("/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, true)));
        RepositoryExplorerViewModel.ShowMessageBox = static (_, _, _, _) => MessageBoxResult.No;

        await viewModel.RestoreCommand.ExecuteAsync(null);

        await mediator.DidNotReceiveWithAnyArgs().Send(default!);
        viewModel.IsLoading.ShouldBeFalse();
    }

    [Test]
    public async Task RestoreCommand_WhenUserConfirms_RestoresFilesAndRefreshesNode()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        var mediator = Substitute.For<IMediator>();

        mediator.CreateStream(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable<RepositoryEntry>(
                new RepositoryFileEntry("/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, true)));
        mediator.CreateStream(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable<ChunkHydrationStatusResult>());
        mediator.Send(Arg.Any<RestoreCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreResult { Success = true, FilesRestored = 1, FilesSkipped = 0, ChunksPendingRehydration = 0 });

        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();
        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger)
        {
            Repository = CreateRepository()
        };

        var node = new TreeNodeViewModel("/", showPlaceholder: false)
        {
            Items = new ObservableCollection<FileItemViewModel>
            {
                new(new RepositoryFileEntry("/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, true))
            }
        };

        viewModel.SelectedTreeNode = node;
        viewModel.SelectedFiles.Add(node.Items.Single());
        RepositoryExplorerViewModel.ShowMessageBox = static (_, _, _, _) => MessageBoxResult.Yes;

        await viewModel.RestoreCommand.ExecuteAsync(null);
        await WaitForAsync(viewModel, () => viewModel.SelectedTreeNode?.Items.Count == 1 && viewModel.SelectedFiles.Count == 0);

        await mediator.Received(1).Send(
            Arg.Is<RestoreCommand>(command =>
                command.Options.RootDirectory == "C:/data" &&
                command.Options.TargetPath == "/file-a.txt" &&
                command.Options.Overwrite &&
                command.Options.NoPointers == false),
            Arg.Any<CancellationToken>());
        viewModel.SelectedFiles.ShouldBeEmpty();
        viewModel.IsLoading.ShouldBeFalse();
    }

    [Test]
    public async Task RestoreCommand_WhenRestoreFails_ShowsErrorAndKeepsSelection()
    {
        var settings = new FakeApplicationSettings();
        var recentRepositoryManager = Substitute.For<IRecentRepositoryManager>();
        var dialogService = Substitute.For<IDialogService>();
        var mediator = Substitute.For<IMediator>();

        mediator.CreateStream(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable<ChunkHydrationStatusResult>());
        mediator.Send(Arg.Any<RestoreCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreResult { Success = false, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = "boom" });

        using var repositorySession = new FakeRepositorySession { Mediator = mediator };
        var logger = new FakeLogger<RepositoryExplorerViewModel>();
        var viewModel = new RepositoryExplorerViewModel(settings, recentRepositoryManager, dialogService, repositorySession, logger)
        {
            Repository = CreateRepository(),
            SelectedTreeNode = new TreeNodeViewModel("/", showPlaceholder: false)
        };

        var selectedFile = new FileItemViewModel(new RepositoryFileEntry("/file-a.txt", ContentHashA, 1024, null, null, true, true, true, true, true));
        viewModel.SelectedFiles.Add(selectedFile);

        string shownMessage = string.Empty;
        RepositoryExplorerViewModel.ShowMessageBox = (message, _, _, _) =>
        {
            shownMessage = message;
            return MessageBoxResult.Yes;
        };

        await viewModel.RestoreCommand.ExecuteAsync(null);

        shownMessage.ShouldContain("Restore failed: boom");
        viewModel.SelectedFiles.ShouldContain(selectedFile);
        viewModel.IsLoading.ShouldBeFalse();
    }

    private static RepositoryOptions CreateRepository()
    {
        return new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "account",
            AccountKeyProtected = "key",
            ContainerName = "container",
            PassphraseProtected = "pass",
        };
    }

    private static async Task WaitForAsync(RepositoryExplorerViewModel viewModel, Func<bool> condition, int timeoutMilliseconds = 1000)
    {
        if (condition())
        {
            return;
        }

        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        void Notify() => signal.Writer.TryWrite(true);
        void OnViewModelPropertyChanged(object? _, PropertyChangedEventArgs __) => Notify();
        void OnRootNodeChanged(object? _, NotifyCollectionChangedEventArgs __) => Notify();
        void OnSelectedFilesChanged(object? _, NotifyCollectionChangedEventArgs __) => Notify();
        void OnSelectedTreeNodePropertyChanged(object? _, PropertyChangedEventArgs __) => Notify();
        void OnSelectedTreeNodeItemsChanged(object? _, NotifyCollectionChangedEventArgs __) => Notify();

        TreeNodeViewModel? subscribedTreeNode = null;

        void AttachSelectedTreeNode(TreeNodeViewModel? node)
        {
            if (ReferenceEquals(subscribedTreeNode, node))
            {
                return;
            }

            if (subscribedTreeNode is not null)
            {
                subscribedTreeNode.PropertyChanged -= OnSelectedTreeNodePropertyChanged;
                subscribedTreeNode.Items.CollectionChanged -= OnSelectedTreeNodeItemsChanged;
            }

            subscribedTreeNode = node;

            if (subscribedTreeNode is not null)
            {
                subscribedTreeNode.PropertyChanged += OnSelectedTreeNodePropertyChanged;
                subscribedTreeNode.Items.CollectionChanged += OnSelectedTreeNodeItemsChanged;
            }
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.RootNode.CollectionChanged += OnRootNodeChanged;
        viewModel.SelectedFiles.CollectionChanged += OnSelectedFilesChanged;
        AttachSelectedTreeNode(viewModel.SelectedTreeNode);

        try
        {
            Notify();
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            while (!condition())
            {
                AttachSelectedTreeNode(viewModel.SelectedTreeNode);

                await signal.Reader.WaitToReadAsync(cancellationTokenSource.Token);
                while (signal.Reader.TryRead(out _))
                {
                    AttachSelectedTreeNode(viewModel.SelectedTreeNode);
                    if (condition())
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.RootNode.CollectionChanged -= OnRootNodeChanged;
            viewModel.SelectedFiles.CollectionChanged -= OnSelectedFilesChanged;
            AttachSelectedTreeNode(null);
        }
    }

    private sealed class FakeRepositorySession : IRepositorySession
    {
        public IMediator Mediator { get; set; }
        public RepositoryOptions Repository { get; set; }
        public int ConnectCalls { get; private set; }

        public Task ConnectAsync(RepositoryOptions repository, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            Repository = repository;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
