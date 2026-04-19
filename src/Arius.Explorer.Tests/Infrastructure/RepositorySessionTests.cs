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
        blobService.GetContainerServiceAsync("container", PreflightMode.ReadOnly, Arg.Any<CancellationToken>()).Returns(blobContainerService);

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
        await blobService.Received(1).GetContainerServiceAsync("container", PreflightMode.ReadOnly, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Dispose_WhenConnected_ClearsMediatorAndRepository()
    {
        var blobServiceFactory = Substitute.For<IBlobServiceFactory>();
        var blobService = Substitute.For<IBlobService>();
        var blobContainerService = Substitute.For<IBlobContainerService>();

        blobServiceFactory.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(blobService);
        blobService.GetContainerServiceAsync(Arg.Any<string>(), Arg.Any<PreflightMode>(), Arg.Any<CancellationToken>()).Returns(blobContainerService);

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

        var metadata = await blobContainerService.GetMetadataAsync("missing");
        var blobs = new List<string>();
        await foreach (var blob in blobContainerService.ListAsync("chunks/"))
        {
            blobs.Add(blob);
        }

        metadata.Exists.ShouldBeFalse();
        blobs.ShouldBeEmpty();
        await blobContainerService.CreateContainerIfNotExistsAsync();
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.DownloadAsync("blob"));
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.OpenWriteAsync("blob"));
        await Should.ThrowAsync<NotSupportedException>(() => blobContainerService.UploadAsync("blob", Stream.Null, new Dictionary<string, string>(), BlobTier.Cool));
    }
}
