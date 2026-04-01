using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Arius.Explorer.ChooseRepository;
using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Extensions;
using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Arius.Explorer.Tests.ChooseRepository;

public class ChooseRepositoryViewModelTests
{
    [Test]
    public void Defaults_AreEmptyAndIdle()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();

        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

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
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

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
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        var blobService = Substitute.For<IBlobService>();

        blobServiceFactory
            .CreateAsync("account", "key", Arg.Any<CancellationToken>())
            .Returns(blobService);

        blobService
            .GetContainerNamesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new[] { "container-a", "container-b" }.ToAsyncEnumerable());

        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

        viewModel.AccountName = "account";
        viewModel.AccountKey = "key";

        await WaitForAsync(() => viewModel.ContainerNames.Count == 2);

        viewModel.StorageAccountError.ShouldBeFalse();
        viewModel.IsLoading.ShouldBeFalse();
        viewModel.ContainerNames.Select(x => x).ShouldBe(["container-a", "container-b"]);
        viewModel.ContainerName.ShouldBe("container-a");

        await blobServiceFactory.Received(1).CreateAsync("account", "key", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AccountCredentials_WhenFactoryThrows_SetsErrorAndClearsContainers()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();

        blobServiceFactory
            .CreateAsync("account", "key", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IBlobService>(new InvalidOperationException("boom")));

        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

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
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

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
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        using var viewModel = new ChooseRepositoryViewModel(blobServiceFactory, TimeSpan.FromMilliseconds(1));

        viewModel.LocalDirectoryPath = "C:/data";
        viewModel.AccountName = "account";
        viewModel.AccountKey = "secret-key";
        viewModel.ContainerName = "valid-container-123";
        viewModel.Passphrase = "secret-pass";

        viewModel.OpenRepositoryCommand.CanExecute(null).ShouldBeTrue();
        viewModel.OpenRepositoryCommand.Execute(null);

        var repository = viewModel.Repository.ShouldNotBeNull();
        repository.LocalDirectoryPath.ShouldBe("C:/data");
        repository.AccountName.ShouldBe("account");
        repository.ContainerName.ShouldBe("valid-container-123");
        repository.AccountKey.ShouldBe("secret-key");
        repository.Passphrase.ShouldBe("secret-pass");
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
}
