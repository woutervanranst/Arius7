using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Shared.Storage;
using NSubstitute;
using Shouldly;

namespace Arius.Core.Tests.Features.ContainerNamesQuery;

public class ContainerNamesQueryHandlerTests
{
    [Test]
    public async Task Handle_StreamsContainerNamesFromBlobService()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        var blobService = Substitute.For<IBlobService>();

        blobServiceFactory
            .CreateAsync("account", "key", Arg.Any<CancellationToken>())
            .Returns(blobService);

        blobService
            .GetContainerNamesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new[] { "repo-a", "repo-b" }.ToAsyncEnumerable());

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IBlobServiceFactory)).Returns(blobServiceFactory);

        var handler = new ContainerNamesQueryHandler(serviceProvider);

        var results = new List<string>();
        await foreach (var name in handler.Handle(new Arius.Core.Features.ContainerNamesQuery.ContainerNamesQuery("account", "key"), CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBe(["repo-a", "repo-b"]);
        await blobServiceFactory.Received(1).CreateAsync("account", "key", Arg.Any<CancellationToken>());
    }
}
