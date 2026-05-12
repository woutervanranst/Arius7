using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Core.Tests.Features.ArchiveCommand;

internal sealed class ArchiveTestEnvironment : IDisposable
{
    private const    string                            AccountName = "test-account";
    private readonly string                            _rootDirectory;
    private readonly string                            _containerName;
    private readonly ChunkIndexService                 _index;
    private readonly PlaintextPassthroughService       _encryption = new();
    private readonly IMediator                         _mediator   = Substitute.For<IMediator>();
    private readonly FakeLogger<ArchiveCommandHandler> _logger     = new();

    public ArchiveTestEnvironment()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"arius-archive-test-{Guid.NewGuid():N}");
        _containerName = $"test-container-{Guid.NewGuid():N}";
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(RepositoryPathStrings.GetChunkIndexCacheDirectory(AccountName, _containerName));
        Directory.CreateDirectory(RepositoryPathStrings.GetFileTreeCacheDirectory(AccountName, _containerName));
        Blobs  = new FakeInMemoryBlobContainerService();
        _index = new ChunkIndexService(Blobs, _encryption, AccountName, _containerName);
    }

    public FakeInMemoryBlobContainerService Blobs { get; }

    public IEncryptionService Encryption => _encryption;

    public string FileTreeCacheDirectory => RepositoryPathStrings.GetFileTreeCacheDirectory(AccountName, _containerName);

    public string RootDirectory => _rootDirectory;

    public FakeLogCollector ArchiveLogs => _logger.Collector;

    public IMediator Mediator => _mediator;

    public byte[] WriteRandomFile(string relativePath, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Random.Shared.NextBytes(content);
        var fullPath = Path.Combine(_rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return content;
    }

    public void SetTimestamps(RelativePath path, DateTimeOffset created, DateTimeOffset modified)
        => new RelativeFileSystem(LocalDirectory.Parse(_rootDirectory)).SetTimestamps(path, created, modified);

    public async Task<FileEntry> ReadRootFileEntryAsync(RelativePath path)
    {
        var fileTreeService = new FileTreeService(Blobs, _encryption, _index, AccountName, _containerName);
        var snapshotSvc = new SnapshotService(Blobs, _encryption, AccountName, _containerName);
        var snapshot = await snapshotSvc.ResolveAsync();
        snapshot.ShouldNotBeNull();

        var entries = await fileTreeService.ReadAsync(snapshot.RootHash);

        if (path.Parent is not { } parentPath)
            return entries.OfType<FileEntry>().Single(entry => entry.Name == path.Name);

        foreach (var segment in parentPath.Segments)
        {
            var directory = entries.OfType<DirectoryEntry>().Single(entry => entry.Name == segment);
            entries = await fileTreeService.ReadAsync(directory.FileTreeHash);
        }

        return entries.OfType<FileEntry>().Single(entry => entry.Name == path.Name);
    }

    public async Task<ArchiveResult> ArchiveAsync(
        BlobTier uploadTier,
        CancellationToken cancellationToken = default,
        bool removeLocal = false,
        bool noPointers = false,
        long? smallFileThreshold = null,
        Func<ChunkHash, long, IProgress<long>>? createUploadProgress = null,
        Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>>? openStagingSession = null)
    {
        Directory.CreateDirectory(RepositoryPathStrings.GetChunkIndexCacheDirectory(AccountName, _containerName));
        Directory.CreateDirectory(RepositoryPathStrings.GetFileTreeCacheDirectory(AccountName, _containerName));

        var fileTreeService = new FileTreeService(Blobs, _encryption, _index, AccountName, _containerName);
        var chunkStorage    = new ChunkStorageService(Blobs, _encryption);
        var snapshotSvc     = new SnapshotService(Blobs, _encryption, AccountName, _containerName);
        Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>> stagingSessionFactory = openStagingSession is not null
            ? openStagingSession
            : OpenStagingSessionAsync;
        var handler         = new ArchiveCommandHandler(
            Blobs,
            _encryption,
            _index,
            chunkStorage,
            fileTreeService,
            snapshotSvc,
            _mediator,
            _logger,
            AccountName,
            _containerName,
            stagingSessionFactory);

        static async Task<IFileTreeStagingSession> OpenStagingSessionAsync(LocalDirectory path, CancellationToken ct)
            => await FileTreeStagingSession.OpenAsync(path, ct);

        return await handler.Handle(
            new Core.Features.ArchiveCommand.ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = _rootDirectory,
                UploadTier = uploadTier,
                SmallFileThreshold = smallFileThreshold ?? 1024 * 1024,
                RemoveLocal = removeLocal,
                NoPointers = noPointers,
                CreateUploadProgress = createUploadProgress,
            }),
            cancellationToken);
    }

    public ShardEntry? Lookup(ContentHash contentHash)
        => _index.LookupAsync(contentHash).GetAwaiter().GetResult();

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);

        RepositoryTestFixture.ResetLocalCacheAsync(AccountName, _containerName).GetAwaiter().GetResult();
    }
}
