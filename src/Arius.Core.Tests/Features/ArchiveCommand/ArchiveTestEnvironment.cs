using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using ArchiveCommandMessage = global::Arius.Core.Features.ArchiveCommand.ArchiveCommand;

namespace Arius.Core.Tests.Features.ArchiveCommand;

internal sealed class ArchiveTestEnvironment : IDisposable
{
    private const string AccountName = "test-account";
    private readonly string _rootDirectory;
    private readonly string _containerName;
    private readonly ChunkIndexService _index;
    private readonly PlaintextPassthroughService _encryption = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly FakeLogger<ArchiveCommandHandler> _logger = new();

    public ArchiveTestEnvironment()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"arius-archive-test-{Guid.NewGuid():N}");
        _containerName = $"test-container-{Guid.NewGuid():N}";
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(AccountName, _containerName));
        Directory.CreateDirectory(FileTreeService.GetDiskCacheDirectory(AccountName, _containerName));
        Blobs = new FakeInMemoryBlobContainerService();
        _index = new ChunkIndexService(Blobs, _encryption, AccountName, _containerName);
    }

    public FakeInMemoryBlobContainerService Blobs { get; }

    public byte[] WriteRandomFile(string relativePath, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Random.Shared.NextBytes(content);
        var fullPath = Path.Combine(_rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return content;
    }

    public string ComputeHash(byte[] content) => Convert.ToHexString(_encryption.ComputeHash(content)).ToLowerInvariant();

    public async Task<ArchiveResult> ArchiveAsync(BlobTier uploadTier)
    {
        Directory.CreateDirectory(RepositoryPaths.GetChunkIndexCacheDirectory(AccountName, _containerName));
        Directory.CreateDirectory(FileTreeService.GetDiskCacheDirectory(AccountName, _containerName));

        var fileTreeService = new FileTreeService(Blobs, _encryption, _index, AccountName, _containerName);
        var chunkStorage = new ChunkStorageService(Blobs, _encryption);
        var snapshotSvc = new SnapshotService(Blobs, _encryption, AccountName, _containerName);
        var handler = new ArchiveCommandHandler(
            Blobs,
            _encryption,
            _index,
            chunkStorage,
            fileTreeService,
            snapshotSvc,
            _mediator,
            _logger,
            AccountName,
            _containerName);

        return await handler.Handle(
            new ArchiveCommandMessage(new ArchiveCommandOptions
            {
                RootDirectory = _rootDirectory,
                UploadTier = uploadTier,
            }),
            default);
    }

    public ShardEntry? Lookup(string contentHash)
        => _index.LookupAsync(contentHash).GetAwaiter().GetResult();

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);

        var chunkIndexDir = RepositoryPaths.GetChunkIndexCacheDirectory(AccountName, _containerName);
        if (Directory.Exists(chunkIndexDir))
            TryDeleteDirectory(chunkIndexDir);

        var treeCacheDir = FileTreeService.GetDiskCacheDirectory(AccountName, _containerName);
        if (Directory.Exists(treeCacheDir))
            TryDeleteDirectory(treeCacheDir);

        var snapshotCacheDir = SnapshotService.GetDiskCacheDirectory(AccountName, _containerName);
        if (Directory.Exists(snapshotCacheDir))
            TryDeleteDirectory(snapshotCacheDir);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
