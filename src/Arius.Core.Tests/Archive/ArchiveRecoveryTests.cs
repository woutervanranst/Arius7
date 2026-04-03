using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Tests.Fakes;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Core.Tests.Archive;

public class ArchiveRecoveryMatrixTests
{
    private const string AccountName = "test-account";

    [Test]
    [MatrixDataSource]
    public async Task Archive_LargeBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("large.bin", 2 * 1024 * 1024);
        var contentHash = env.ComputeHash(content);

        await env.Blobs.SeedLargeBlobAsync(BlobPaths.Chunk(contentHash), content, uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(contentHash));

        var result = await env.ArchiveAsync(uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Lookup(contentHash).ShouldNotBeNull();
    }

    [Test]
    [MatrixDataSource]
    public async Task Archive_TarBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("small.txt", 256);
        var contentHash = env.ComputeHash(content);

        var tarHash = env.ComputeHash(content); // single small file tar hash seed is arbitrary for the fake path
        await env.Blobs.SeedTarBlobAsync(BlobPaths.Chunk(tarHash), [content], uploadTier);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(tarHash));

        var result = await env.ArchiveAsync(uploadTier);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Lookup(contentHash).ShouldNotBeNull();
    }

    [Test]
    public async Task Archive_LargeBlobWithoutMetadata_Rerun_DeletesAndRetries()
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("partial.bin", 2 * 1024 * 1024);
        var contentHash = env.ComputeHash(content);
        var blobName = BlobPaths.Chunk(contentHash);

        await env.Blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        env.Blobs.ClearMetadata(blobName);
        env.Blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await env.ArchiveAsync(BlobTier.Archive);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        env.Blobs.DeletedBlobNames.ShouldContain(blobName);

        var finalMeta = await env.Blobs.GetMetadataAsync(blobName);
        finalMeta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    private sealed class ArchiveTestEnvironment : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly string _containerName;
        private readonly ChunkIndexService _index;
        private readonly PlaintextPassthroughService _encryption = new();
        private readonly IMediator _mediator = Substitute.For<IMediator>();

        public ArchiveTestEnvironment()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"arius-archive-test-{Guid.NewGuid():N}");
            _containerName = $"test-container-{Guid.NewGuid():N}";
            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(ChunkIndexService.GetL2Directory(AccountName, _containerName));
            Directory.CreateDirectory(TreeBuilder.GetDiskCacheDirectory(AccountName, _containerName));
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
            Directory.CreateDirectory(ChunkIndexService.GetL2Directory(AccountName, _containerName));
            Directory.CreateDirectory(TreeBuilder.GetDiskCacheDirectory(AccountName, _containerName));

            var handler = new ArchiveCommandHandler(
                Blobs,
                _encryption,
                _index,
                _mediator,
                NullLogger<ArchiveCommandHandler>.Instance,
                AccountName,
                _containerName);

            return await handler.Handle(
                new ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = _rootDirectory,
                    UploadTier = uploadTier,
                }),
                default);
        }

        public ShardEntry? Lookup(string contentHash)
        {
            var result = _index.LookupAsync([contentHash]).GetAwaiter().GetResult();
            return result.TryGetValue(contentHash, out var entry) ? entry : null;
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootDirectory))
                Directory.Delete(_rootDirectory, recursive: true);

            var chunkIndexDir = ChunkIndexService.GetL2Directory(AccountName, _containerName);
            if (Directory.Exists(chunkIndexDir))
                TryDeleteDirectory(chunkIndexDir);

            var treeCacheDir = TreeBuilder.GetDiskCacheDirectory(AccountName, _containerName);
            if (Directory.Exists(treeCacheDir))
                TryDeleteDirectory(treeCacheDir);
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
}
