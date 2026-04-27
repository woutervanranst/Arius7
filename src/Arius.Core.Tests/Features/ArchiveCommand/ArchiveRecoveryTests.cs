using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Features.ArchiveCommand;

public class ArchiveRecoveryTests
{
    private const string AccountName = "test-account";

    [Test]
    [MatrixDataSource]
    public async Task Archive_LargeBlobAlreadyExistsWithMetadata_Rerun_Continues(
        [Matrix(BlobTier.Archive, BlobTier.Cold)] BlobTier uploadTier)
    {
        using var env = new ArchiveTestEnvironment();
        var content = env.WriteRandomFile("large.bin", 2 * 1024 * 1024);
        var contentHash = env.Encryption.ComputeHash(content).ToString();

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
        var contentHash = env.Encryption.ComputeHash(content).ToString();

        var tarHash = env.Encryption.ComputeHash(content).ToString();
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
        var contentHash = env.Encryption.ComputeHash(content).ToString();
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

}
