using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Core.Tests.Restore;

public class RestoreCommandHandlerTests
{
    [Test]
    public async Task Handle_MissingContainer_DoesNotAttemptToCreateContainer()
    {
        var blobs = new ThrowOnCreateBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        using var index = new ChunkIndexService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var treeCache = new TreeCacheService(blobs, encryption, index, "acct-restore-missing", "ctr-restore-missing");
        var snapshotSvc = new SnapshotService(blobs, encryption, "acct-restore-missing", "ctr-restore-missing");
        var mediator = Substitute.For<IMediator>();

        var handler = new RestoreCommandHandler(
            blobs,
            encryption,
            index,
            treeCache,
            snapshotSvc,
            mediator,
            NullLogger<RestoreCommandHandler>.Instance,
            "acct-restore-missing",
            "ctr-restore-missing");

        var result = await handler.Handle(
            new RestoreCommand(new RestoreOptions { RootDirectory = Path.GetTempPath() }),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No snapshots found in this repository.");
        blobs.CreateCalled.ShouldBeFalse();
    }

    private sealed class ThrowOnCreateBlobContainerService : IBlobContainerService
    {
        public bool CreateCalled { get; private set; }

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            throw new InvalidOperationException("CreateContainerIfNotExistsAsync should not be called by restore.");
        }

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlobMetadata { Exists = false });

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
