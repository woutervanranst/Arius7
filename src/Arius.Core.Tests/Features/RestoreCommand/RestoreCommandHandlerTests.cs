using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using Shouldly;
using RestoreCommandMessage = global::Arius.Core.Features.RestoreCommand.RestoreCommand;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreCommandHandlerTests
{
    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService("restore");
        var encryption = new PlaintextPassthroughService();
        using var index = new ChunkIndexService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var fileTreeService = new FileTreeService(blobs, encryption, index, "acct-restore-missing", "ctr-restore-missing");
        var snapshotSvc = new SnapshotService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var mediator = Substitute.For<IMediator>();
        var logger = new FakeLogger<RestoreCommandHandler>();

        var handler = new RestoreCommandHandler(
            encryption,
            index,
            new ChunkStorageService(blobs, encryption),
            fileTreeService,
            snapshotSvc,
            mediator,
            logger,
            "acct-restore-missing",
            "ctr-restore-missing");

        var result = await handler.Handle(
            new RestoreCommandMessage(new RestoreOptions { RootDirectory = Path.GetTempPath() }),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No snapshots found in this repository.");
        blobs.CreateCalled.ShouldBeFalse();
    }

}
