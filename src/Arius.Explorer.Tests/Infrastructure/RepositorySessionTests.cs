using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Arius.Core.Shared.Storage;
using Arius.Explorer.Infrastructure;
using Arius.Explorer.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Arius.Explorer.Tests.Infrastructure;

public class RepositorySessionTests
{
    [Test]
    public async Task ConnectAsync_WhenCalled_InitializesMediatorAndRepository()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        var blobService = Substitute.For<IBlobService>();
        var blobContainerService = Substitute.For<IBlobContainerService>();

        blobServiceFactory.CreateAsync("account", "key", Arg.Any<CancellationToken>()).Returns(blobService);
        blobService.OpenContainerServiceAsync("container", PreflightMode.ReadOnly, Arg.Any<CancellationToken>()).Returns(blobContainerService);

        var services = new ServiceCollection();
        services.AddSingleton(blobServiceFactory);
        services.AddLogging();

        await using var provider = services.BuildServiceProvider();
        using var session = new RepositorySession(provider);

        var repository = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "account",
            AccountKeyProtected = "key",
            ContainerName = "container",
            PassphraseProtected = "pass",
        };

        await session.ConnectAsync(repository);

        session.Repository.ShouldBe(repository);
        session.Mediator.ShouldNotBeNull();
        await blobServiceFactory.Received(1).CreateAsync("account", "key", Arg.Any<CancellationToken>());
        await blobService.Received(1).OpenContainerServiceAsync("container", PreflightMode.ReadOnly, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Dispose_WhenConnected_ClearsMediatorAndRepository()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        var blobService = Substitute.For<IBlobService>();
        var blobContainerService = Substitute.For<IBlobContainerService>();

        blobServiceFactory.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(blobService);
        blobService.OpenContainerServiceAsync(Arg.Any<string>(), Arg.Any<PreflightMode>(), Arg.Any<CancellationToken>()).Returns(blobContainerService);

        var services = new ServiceCollection();
        services.AddSingleton(blobServiceFactory);
        services.AddLogging();

        await using var provider = services.BuildServiceProvider();
        using var session = new RepositorySession(provider);

        await session.ConnectAsync(new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "account",
            AccountKeyProtected = "key",
            ContainerName = "container",
            PassphraseProtected = "pass",
        });

        session.Dispose();

        session.Repository.ShouldBeNull();
        session.Mediator.ShouldBeNull();
    }

    [Test]
    public async Task AddRootCorePlaceholders_RegistersBlobContainerPlaceholderThatReportsNoBlobs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RepositorySession.AddRootCorePlaceholders(services);

        await using var provider = services.BuildServiceProvider();
        var blobContainerService = provider.GetRequiredService<IBlobContainerService>();

        var metadata = await blobContainerService.GetMetadataAsync(RelativePath.Parse("missing"));
        var blobs = new List<RelativePath>();
        await foreach (var item in blobContainerService.ListAsync(RelativePath.Parse("chunks"), includeMetadata: false))
        {
            blobs.Add(item.Name);
        }

        metadata.Exists.ShouldBeFalse();
        blobs.ShouldBeEmpty();
        await blobContainerService.CreateContainerIfNotExistsAsync();
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.DownloadAsync(RelativePath.Parse("blob")));
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.OpenWriteAsync(RelativePath.Parse("blob")));
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.UploadAsync(RelativePath.Parse("blob"), Stream.Null, new Dictionary<string, string>(), BlobTier.Cool));
    }
}
