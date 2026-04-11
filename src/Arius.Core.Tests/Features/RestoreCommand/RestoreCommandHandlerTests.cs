using Arius.Core.Features.RestoreCommand;
using RestoreCommandMessage = global::Arius.Core.Features.RestoreCommand.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreCommandHandlerTests
{
    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        using var index = new ChunkIndexService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var fileTreeService = new FileTreeService(blobs, encryption, index, "acct-restore-missing", "ctr-restore-missing");
        var snapshotSvc = new SnapshotService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var mediator = Substitute.For<IMediator>();

        var handler = new RestoreCommandHandler(
            encryption,
            index,
            new ChunkStorageService(blobs, encryption),
            fileTreeService,
            snapshotSvc,
            mediator,
            NullLogger<RestoreCommandHandler>.Instance,
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
