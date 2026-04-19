using System;
using System.Collections.Generic;
using System.Linq;
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

        await WaitForAsync(() => viewModel.ContainerNames.Count == 2);

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

        await WaitForAsync(() => viewModel.StorageAccountError);

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

        WaitForAsync(() => viewModel.ContainerNames.Count == 1).GetAwaiter().GetResult();

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

        await WaitForAsync(() => queryInvoked && !viewModel.IsLoading);

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

        await WaitForAsync(() => queryInvoked && viewModel.ContainerNames.Count == 2);

        viewModel.ContainerName.ShouldBe("container-b");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 1000)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        var start = DateTime.UtcNow;

        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(25);
        }
    }

    private static async IAsyncEnumerable<string> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}
