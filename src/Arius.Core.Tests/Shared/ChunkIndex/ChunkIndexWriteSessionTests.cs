using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexWriteSessionTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task FlushAsync_Success_ClearsSessionOverlayAndPendingEntries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("write-success");
        var session = new ChunkIndexWriteSession();
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        session.AddEntry(entry);

        await session.FlushAsync(CreateCache(blobs, repositoryKey));

        session.TryLookup(entry.ContentHash, out _).ShouldBeFalse();
        await session.FlushAsync(CreateCache(blobs, repositoryKey));
        blobs.UploadedBlobNames.Count.ShouldBe(1);
    }

    [Test]
    public async Task FlushAsync_Failure_KeepsSessionOverlayAndPendingEntriesForRetry()
    {
        var repositoryKey = UniqueRepositoryKey("write-failure");
        var session = new ChunkIndexWriteSession();
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        session.AddEntry(entry);
        var failingBlobs = new ThrowOnUploadBlobContainerService();

        await Should.ThrowAsync<InvalidOperationException>(() => session.FlushAsync(CreateCache(failingBlobs, repositoryKey)));

        session.TryLookup(entry.ContentHash, out var sessionEntry).ShouldBeTrue();
        sessionEntry.ShouldBe(entry);

        var retryBlobs = new FakeInMemoryBlobContainerService();
        await session.FlushAsync(CreateCache(retryBlobs, repositoryKey));
        retryBlobs.UploadedBlobNames.Count.ShouldBe(1);
    }

    [Test]
    public void AddEntry_ConcurrentCalls_RecordSessionOverlaySafely()
    {
        var session = new ChunkIndexWriteSession();
        var entries = Enumerable.Range(0, 64)
            .Select(i => new ShardEntry(ContentHash.Parse($"aa{i:x2}{new string('0', 60)}"), FakeChunkHash('b'), i, i))
            .ToArray();

        Parallel.ForEach(entries, session.AddEntry);

        foreach (var entry in entries)
        {
            session.TryLookup(entry.ContentHash, out var actual).ShouldBeTrue();
            actual.ShouldBe(entry);
        }
    }

    [Test]
    public async Task AddEntry_DuringFlush_Throws()
    {
        var repositoryKey = UniqueRepositoryKey("write-flush-gate");
        var blobs = new BlockingUploadBlobContainerService();
        var session = new ChunkIndexWriteSession();
        var entry = new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5);
        session.AddEntry(entry);

        var flushTask = session.FlushAsync(CreateCache(blobs, repositoryKey));
        await blobs.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var concurrentEntry = new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 20, 10);
        Should.Throw<InvalidOperationException>(() => session.AddEntry(concurrentEntry));

        blobs.AllowUpload.SetResult();
        await flushTask;
    }

    private static ChunkIndexShardCache CreateCache(IBlobContainerService blobs, string repositoryKey)
    {
        var l2 = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        l2.CreateDirectory(RelativePath.Root);
        return new ChunkIndexShardCache(blobs, s_encryption, l2, ChunkIndexService.DefaultL1CacheBudgetBytes);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";

    private sealed class ThrowOnUploadBlobContainerService : IBlobContainerService
    {
        private readonly FakeInMemoryBlobContainerService _inner = new();

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => _inner.CreateContainerIfNotExistsAsync(cancellationToken);

        public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("upload failed");

        public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

        public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.DownloadAsync(blobName, cancellationToken);

        public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.TryDownloadAsync(blobName, cancellationToken);

        public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(blobName, cancellationToken);

        public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, includeMetadata, cancellationToken);

        public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

        public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => _inner.SetTierAsync(blobName, tier, cancellationToken);

        public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

        public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.DeleteAsync(blobName, cancellationToken);
    }

    private sealed class BlockingUploadBlobContainerService : IBlobContainerService
    {
        private readonly FakeInMemoryBlobContainerService _inner = new();

        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowUpload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => _inner.CreateContainerIfNotExistsAsync(cancellationToken);

        public async Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            UploadStarted.SetResult();
            await AllowUpload.Task.WaitAsync(cancellationToken);
            await _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);
        }

        public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

        public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.DownloadAsync(blobName, cancellationToken);

        public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.TryDownloadAsync(blobName, cancellationToken);

        public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(blobName, cancellationToken);

        public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, includeMetadata, cancellationToken);

        public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

        public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => _inner.SetTierAsync(blobName, tier, cancellationToken);

        public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

        public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => _inner.DeleteAsync(blobName, cancellationToken);
    }
}
