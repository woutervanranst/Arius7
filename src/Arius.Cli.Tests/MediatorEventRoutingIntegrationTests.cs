using Arius.Core;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies that when the CLI's notification handlers are registered via DI
/// and a real <see cref="IMediator"/> is used, published archive/restore events
/// are routed to <see cref="ProgressState"/> correctly.
/// </summary>
public class MediatorEventRoutingIntegrationTests
{
    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerProvider.Instance));
        services.AddSingleton<ProgressState>();

        // AddMediator() must be called in the outermost (test) assembly so the source
        // generator discovers INotificationHandler<T> implementations in both Core and CLI.
        services.AddMediator();

        // Provide stub Core services so the source-generated Mediator can initialize.
        services.AddArius(
            blobContainer:      Substitute.For<IBlobContainerService>(),
            passphrase:       null,
            accountName:      "test",
            containerName:    "test",
            cacheBudgetBytes: 0);

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AllArchiveEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        // Publish all archive notification events in pipeline order
        await mediator.Publish(new FileScannedEvent("a.bin", 100));
        await mediator.Publish(new ScanCompleteEvent(1, 100));
        await mediator.Publish(new FileHashingEvent("a.bin", 100));
        await mediator.Publish(new FileHashedEvent("a.bin", FakeContentHash('a')));
        await mediator.Publish(new TarBundleStartedEvent());
        await mediator.Publish(new TarEntryAddedEvent(FakeContentHash('a'), 1, 100));
        await mediator.Publish(new TarBundleSealingEvent(1, 100, FakeChunkHash('b'), [FakeContentHash('a')]));
        await mediator.Publish(new ChunkUploadingEvent(FakeChunkHash('b'), 100));
        await mediator.Publish(new TarBundleUploadedEvent(FakeChunkHash('b'), 80, 1));
        await mediator.Publish(new SnapshotCreatedEvent(FakeFileTreeHash('c'), DateTimeOffset.UtcNow, 1));

        // Verify ProgressState was updated
        state.FilesScanned.ShouldBe(1L);
        state.TotalFiles.ShouldBe(1L);
        state.ScanComplete.ShouldBeTrue();
        state.FilesHashed.ShouldBe(1L);
        state.TarsUploaded.ShouldBe(1L);
        // a.bin was removed after tar entry added
        state.TrackedFiles.ContainsKey("a.bin").ShouldBeFalse();
        state.SnapshotComplete.ShouldBeTrue();
    }

    [Test]
    public async Task AllRestoreEvents_UpdateProgressState_ViaMediator()
    {
        var sp       = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();
        var state    = sp.GetRequiredService<ProgressState>();

        await mediator.Publish(new RestoreStartedEvent(10));
        await mediator.Publish(new FileRestoredEvent("a.txt", 1000L));
        await mediator.Publish(new FileRestoredEvent("b.txt", 2000L));
        await mediator.Publish(new FileSkippedEvent("c.txt", 500L));
        await mediator.Publish(new RehydrationStartedEvent(4, 2048));

        state.RestoreTotalFiles.ShouldBe(10);
        state.FilesRestored.ShouldBe(2L);
        state.BytesRestored.ShouldBe(3000L);
        state.FilesSkipped.ShouldBe(1L);
        state.BytesSkipped.ShouldBe(500L);
        state.RehydrationChunkCount.ShouldBe(4);
    }
}
