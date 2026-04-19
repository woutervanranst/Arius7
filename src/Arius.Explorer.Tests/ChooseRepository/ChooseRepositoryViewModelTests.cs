using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Explorer.ChooseRepository;
using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Extensions;
using Mediator;
using NSubstitute;

namespace Arius.Explorer.Tests.ChooseRepository;

public class ChooseRepositoryViewModelTests
{
    [Test]
    public void Defaults_AreEmptyAndIdle()
    {
        var mediator = Substitute.For<IMediator>();

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.Repository.ShouldBeNull();
        viewModel.LocalDirectoryPath.ShouldBe(string.Empty);
        viewModel.AccountName.ShouldBe(string.Empty);
        viewModel.AccountKey.ShouldBe(string.Empty);
        viewModel.ContainerName.ShouldBe(string.Empty);
        viewModel.Passphrase.ShouldBe(string.Empty);
        viewModel.ContainerNames.ShouldBeEmpty();
        viewModel.IsLoading.ShouldBeFalse();
        viewModel.StorageAccountError.ShouldBeFalse();
    }

    [Test]
    public void SettingRepository_PopulatesViewModelFields()
    {
        var mediator = Substitute.For<IMediator>();
        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        var repository = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "account",
            AccountKeyProtected = "account-key".Protect(),
            ContainerName = "container",
            PassphraseProtected = "passphrase".Protect(),
        };

        viewModel.Repository = repository;

        viewModel.LocalDirectoryPath.ShouldBe("C:/data");
        viewModel.AccountName.ShouldBe("account");
        viewModel.AccountKey.ShouldBe("account-key");
        viewModel.ContainerName.ShouldBe("container");
        viewModel.Passphrase.ShouldBe("passphrase");
    }

    [Test]
    public async Task AccountCredentials_WhenQuerySucceeds_LoadsContainerNamesAndSelectsFirst()
    {
        var mediator = Substitute.For<IMediator>();

        mediator
            .CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "key"), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { "container-a", "container-b" }.ToAsyncEnumerable());

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";

        await WaitForAsync(viewModel, () =>
            viewModel.ContainerNames.Count == 2 &&
            viewModel.ContainerName == "container-a" &&
            !viewModel.IsLoading);

        viewModel.StorageAccountError.ShouldBeFalse();
        viewModel.IsLoading.ShouldBeFalse();
        viewModel.ContainerNames.Select(x => x).ShouldBe(["container-a", "container-b"]);
        viewModel.ContainerName.ShouldBe("container-a");

        mediator.Received(1).CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "key"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AccountCredentials_WhenFactoryThrows_SetsErrorAndClearsContainers()
    {
        var mediator = Substitute.For<IMediator>();

        mediator
            .CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "key"), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";

        await WaitForAsync(viewModel, () =>
            viewModel.StorageAccountError &&
            !viewModel.IsLoading &&
            viewModel.ContainerNames.Count == 0 &&
            viewModel.ContainerName == string.Empty);

        viewModel.IsLoading.ShouldBeFalse();
        viewModel.StorageAccountError.ShouldBeTrue();
        viewModel.ContainerNames.ShouldBeEmpty();
        viewModel.ContainerName.ShouldBe(string.Empty);
    }

    [Test]
    public void OpenRepositoryCommand_WhenConfigurationIsInvalid_IsDisabled()
    {
        var mediator = Substitute.For<IMediator>();
        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.LocalDirectoryPath = "C:/data";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";
        viewModel.ContainerName = "MyContainer";
        viewModel.Passphrase = "pass";

        viewModel.OpenRepositoryCommand.CanExecute(null).ShouldBeFalse();
    }

    [Test]
    public void OpenRepositoryCommand_WhenAllFieldsAreValid_IsEnabledAndBuildsRepository()
    {
        var mediator = Substitute.For<IMediator>();

        mediator
            .CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "secret-key"), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { "valid-container-123" }.ToAsyncEnumerable());

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.LocalDirectoryPath = "C:/data";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "secret-key";
        viewModel.Passphrase = "secret-pass";

        WaitForAsync(viewModel, () =>
            viewModel.ContainerNames.Count == 1 &&
            viewModel.ContainerName == "valid-container-123" &&
            !viewModel.IsLoading).GetAwaiter().GetResult();

        viewModel.OpenRepositoryCommand.CanExecute(null).ShouldBeTrue();
        viewModel.OpenRepositoryCommand.Execute(null);

        var repository = viewModel.Repository.ShouldNotBeNull();
        repository.LocalDirectoryPath.ShouldBe("C:/data");
        repository.AccountName.ShouldBe("account");
        repository.ContainerName.ShouldBe("valid-container-123");
        repository.AccountKey.ShouldBe("secret-key");
        repository.Passphrase.ShouldBe("secret-pass");
    }

    [Test]
    public void OpenRepositoryCommand_WhenContainerNameViolatesAzureRules_IsDisabled()
    {
        var mediator = Substitute.For<IMediator>();
        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.LocalDirectoryPath = "C:/data";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";
        viewModel.Passphrase = "pass";

        foreach (var invalidContainerName in new[] { "ab", "-abc", "abc-", "ab--cd", "Abc" })
        {
            viewModel.ContainerName = invalidContainerName;
            viewModel.OpenRepositoryCommand.CanExecute(null).ShouldBeFalse($"{invalidContainerName} should be rejected");
        }
    }

    [Test]
    public async Task AccountCredentials_WhenQueryReturnsNoContainers_ClearsContainerSelection()
    {
        var mediator = Substitute.For<IMediator>();
        var queryInvoked = false;

        mediator
            .CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "key"), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                queryInvoked = true;
                return EmptyAsyncEnumerable();
            });

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.ContainerName = "existing-container";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";

        await WaitForAsync(viewModel, () =>
            queryInvoked &&
            !viewModel.IsLoading &&
            viewModel.ContainerNames.Count == 0 &&
            viewModel.ContainerName == string.Empty);

        viewModel.StorageAccountError.ShouldBeFalse();
        viewModel.ContainerName.ShouldBe(string.Empty);
    }

    [Test]
    public async Task AccountCredentials_WhenCurrentContainerStillExists_PreservesSelection()
    {
        var mediator = Substitute.For<IMediator>();
        var queryInvoked = false;

        mediator
            .CreateStream<string>(Arg.Is<ContainerNamesQuery>(q => q.AccountName == "account" && q.AccountKey == "key"), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                queryInvoked = true;
                return new[] { "container-a", "container-b" }.ToAsyncEnumerable();
            });

        using var viewModel = new ChooseRepositoryViewModel(mediator, TimeSpan.FromMilliseconds(1));

        viewModel.ContainerName = "container-b";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";

        await WaitForAsync(viewModel, () =>
            queryInvoked &&
            viewModel.ContainerNames.Count == 2 &&
            viewModel.ContainerName == "container-b" &&
            !viewModel.IsLoading);

        viewModel.ContainerName.ShouldBe("container-b");
    }

    private static void Signal(Channel<bool> signal)
    {
        signal.Writer.TryWrite(true);
    }

    private static async Task WaitForAsync(ChooseRepositoryViewModel viewModel, Func<bool> condition, int timeoutMilliseconds = 1000)
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

        void OnPropertyChanged(object? _, PropertyChangedEventArgs __) => Signal(signal);
        void OnContainerNamesChanged(object? _, NotifyCollectionChangedEventArgs __) => Signal(signal);

        viewModel.PropertyChanged += OnPropertyChanged;
        viewModel.ContainerNames.CollectionChanged += OnContainerNamesChanged;

        try
        {
            Signal(signal);
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            while (!condition())
            {
                await signal.Reader.WaitToReadAsync(cancellationTokenSource.Token);
                while (signal.Reader.TryRead(out _))
                {
                    if (condition())
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
            viewModel.ContainerNames.CollectionChanged -= OnContainerNamesChanged;
        }
    }

    private static async IAsyncEnumerable<string> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}
