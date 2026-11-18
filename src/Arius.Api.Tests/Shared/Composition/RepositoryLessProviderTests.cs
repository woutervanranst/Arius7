using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Arius.Api.Tests.Shared.Composition;

/// <summary>
/// The production composition (real Mediator + real Core registration) must be able to serve the one Core
/// request that needs no repository: listing an account's containers.
/// </summary>
public class RepositoryLessProviderTests
{
    [Test]
    public async Task The_repository_less_provider_lists_an_accounts_containers_through_the_mediator()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"arius-regtest-{Guid.NewGuid():N}", "app.sqlite");
        var secrets      = new SecretProtector(DataProtectionProvider.Create(nameof(RepositoryLessProviderTests)));
        var composer     = new AzureRepositoryCoreComposer(new FakeBlobServiceFactory());

        await using var registry = new RepositoryProviderRegistry(new AppDatabase(databasePath), secrets, composer, NullLoggerFactory.Instance);

        var mediator = registry.GetRepositoryLessProvider().GetRequiredService<IMediator>();
        var names    = await mediator.CreateStream(new ContainerNamesQuery("account", "key")).ToListAsync();

        names.ShouldBe(["account/one", "account/two"]);
    }

    private sealed class FakeBlobServiceFactory : IBlobServiceFactory
    {
        public Task<IBlobService> CreateAsync(string accountName, string? accountKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IBlobService>(new FakeBlobService(accountName));
    }

    private sealed class FakeBlobService(string accountName) : IBlobService
    {
        public IAsyncEnumerable<string> GetContainerNamesAsync(CancellationToken cancellationToken = default) =>
            new[] { $"{accountName}/one", $"{accountName}/two" }.ToAsyncEnumerable();

        public Task<IBlobContainerService> OpenContainerServiceAsync(string containerName, PreflightMode preflightMode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
