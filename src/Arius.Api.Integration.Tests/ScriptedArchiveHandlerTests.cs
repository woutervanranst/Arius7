using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Testing;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Mediator;
using NSubstitute;

namespace Arius.Api.Integration.Tests;

public class ScriptedArchiveHandlerTests
{
    [Test]
    public async Task Publishes_events_in_order_then_returns_result()
    {
        var publisher = Substitute.For<IPublisher>();
        var scan = new ScanCompleteEvent(2, 3000);
        var uploaded = new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), 400, 2000);
        var result = NewArchiveResult();
        var handler = new ScriptedArchiveHandler(publisher, new ArchiveScenario([scan, uploaded], result), new ScenarioGate(), new ScenarioContext(RepositoryId: 1));

        var actual = await handler.Handle(new ArchiveCommand(new ArchiveCommandOptions { RootDirectory = "/x" }), default);

        await Assert.That(actual).IsSameReferenceAs(result);
        Received.InOrder(() =>
        {
            publisher.Publish(scan, Arg.Any<CancellationToken>());
            publisher.Publish(uploaded, Arg.Any<CancellationToken>());
        });
    }

    private static ArchiveResult NewArchiveResult() => new()
    {
        Success = true, FilesScanned = 2, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
        OriginalSize = 3000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
        FastHashRehashed = 2, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
    };
}
