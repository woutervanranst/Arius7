using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Arius.AzureBlob.Tests.Fakes;

public sealed class FakeBlobClient(FakeContainer container, string blobName) : BlobClient
{
    public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
    {
        // Azure returns 404 ContainerNotFound when uploading a blob into a container that does not exist.
        if (!container.Exists)
        {
            throw new RequestFailedException(404, "Container not found");
        }

        if (blobName == ".arius-preflight-probe")
        {
            container.UploadedProbe = true;
        }

        return Task.FromResult(Response.FromValue(
            BlobsModelFactory.BlobContentInfo(new ETag("\"etag-upload\""), default, default, default, default, default, default),
            FakeResponse.Instance));
    }

    public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        // Azure returns 404 ContainerNotFound when uploading a blob into a container that does not exist.
        if (!container.Exists)
        {
            throw new RequestFailedException(404, "Container not found");
        }

        if (blobName == ".arius-preflight-probe")
        {
            container.UploadedProbe = true;
        }

        return Task.FromResult(Response.FromValue(
            BlobsModelFactory.BlobContentInfo(default, default, default, default, default, default, default),
            FakeResponse.Instance));
    }

    public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions conditions = null!, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Response.FromValue(
            BlobsModelFactory.BlobProperties(
                lastModified: DateTimeOffset.UtcNow,
                contentLength: 3,
                accessTier: AccessTier.Cool.ToString(),
                eTag: container.MetadataEtag,
                metadata: new Dictionary<string, string>()),
            FakeResponse.Instance));
    }

    public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions conditions = null!, CancellationToken cancellationToken = default)
    {
        if (blobName == ".arius-preflight-probe")
        {
            container.DeletedProbe = true;
        }

        return Task.FromResult<Response>(FakeResponse.Instance);
    }
}