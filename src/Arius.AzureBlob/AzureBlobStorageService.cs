using Arius.Core.Storage;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using AzureRehydratePriority = Azure.Storage.Blobs.Models.RehydratePriority;
using CoreRehydratePriority = Arius.Core.Storage.RehydratePriority;

namespace Arius.AzureBlob;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobStorageService"/>.
/// Operates against a single Azure Blob container.
/// </summary>
public sealed class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    public AzureBlobStorageService(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task UploadAsync(
        string                              blobName,
        Stream                              content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier                            tier,
        string?                             contentType       = null,
        bool                                overwrite         = false,
        CancellationToken                   cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);

        var uploadOptions = new BlobUploadOptions
        {
            Metadata    = new Dictionary<string, string>(metadata),
            AccessTier  = ToAzureTier(tier),
            Conditions  = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
        };

        if (contentType is not null)
        {
            uploadOptions.HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            };
        }

        await blobClient.UploadAsync(content, uploadOptions, cancellationToken);
    }

    public async Task<Stream> OpenWriteAsync(
        string            blobName,
        string?           contentType       = null,
        BlobTier          tier              = BlobTier.Hot,
        bool              overwrite         = false,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlockBlobClient(blobName);

        var openWriteOptions = new BlockBlobOpenWriteOptions
        {
            HttpHeaders = contentType is not null
                ? new BlobHttpHeaders { ContentType = contentType }
                : null,
        };

        var writeStream = await blobClient.OpenWriteAsync(overwrite, openWriteOptions, cancellationToken);

        // BlockBlobOpenWriteOptions does not support AccessTier; wrap to set tier on close.
        return tier == BlobTier.Hot
            ? writeStream
            : new TierSettingStream(writeStream, blobClient, ToAzureTier(tier));
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public async Task<Stream> DownloadAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        var response   = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    // ── HEAD ──────────────────────────────────────────────────────────────────

    public async Task<BlobMetadata> GetMetadataAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        try
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var p     = props.Value;

            return new BlobMetadata
            {
                Exists        = true,
                Tier          = FromAzureTier(p.AccessTier),
                ContentLength = p.ContentLength,
                IsRehydrating = p.ArchiveStatus is "rehydrate-pending-to-hot" or "rehydrate-pending-to-cool",
                Metadata      = (IReadOnlyDictionary<string, string>)p.Metadata
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new BlobMetadata { Exists = false };
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> ListAsync(
        string            prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in _container.GetBlobsAsync(
                           traits: BlobTraits.None,
                           states: BlobStates.None,
                           prefix: prefix,
                           cancellationToken: cancellationToken))
            yield return item.Name;
    }

    // ── Metadata update ───────────────────────────────────────────────────────

    public async Task SetMetadataAsync(
        string                              blobName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken                   cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.SetMetadataAsync(
            new Dictionary<string, string>(metadata),
            cancellationToken: cancellationToken);
    }

    // ── Copy (rehydration) ────────────────────────────────────────────────────

    public async Task CopyAsync(
        string                    sourceBlobName,
        string                    destinationBlobName,
        BlobTier                  destinationTier,
        CoreRehydratePriority?    rehydratePriority = null,
        CancellationToken         cancellationToken = default)
    {
        var sourceUri = _container.GetBlobClient(sourceBlobName).Uri;
        var destBlob  = _container.GetBlobClient(destinationBlobName);

        var copyOptions = new BlobCopyFromUriOptions
        {
            AccessTier        = ToAzureTier(destinationTier),
            RehydratePriority = rehydratePriority.HasValue
                                    ? ToAzureRehydratePriority(rehydratePriority.Value)
                                    : null
        };

        await destBlob.StartCopyFromUriAsync(sourceUri, copyOptions, cancellationToken);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    // ── Tier conversion helpers ───────────────────────────────────────────────

    private static AccessTier ToAzureTier(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => AccessTier.Hot,
        BlobTier.Cool    => AccessTier.Cool,
        BlobTier.Cold    => AccessTier.Cold,
        BlobTier.Archive => AccessTier.Archive,
        _                => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    private static BlobTier? FromAzureTier(AccessTier? tier)
    {
        if (tier is null)               return null;
        if (tier == AccessTier.Hot)     return BlobTier.Hot;
        if (tier == AccessTier.Cool)    return BlobTier.Cool;
        if (tier == AccessTier.Cold)    return BlobTier.Cold;
        if (tier == AccessTier.Archive) return BlobTier.Archive;
        return null;
    }

    private static AzureRehydratePriority ToAzureRehydratePriority(CoreRehydratePriority p) => p switch
    {
        CoreRehydratePriority.Standard => AzureRehydratePriority.Standard,
        CoreRehydratePriority.High     => AzureRehydratePriority.High,
        _                              => throw new ArgumentOutOfRangeException(nameof(p), p, null)
    };
}

// ── Tier-setting stream wrapper ────────────────────────────────────────────────

/// <summary>
/// Wraps an Azure write stream and calls <c>SetAccessTierAsync</c> on the blob
/// after the stream is disposed (i.e., after the upload block list is committed).
/// This is needed because <see cref="BlockBlobOpenWriteOptions"/> does not expose
/// an AccessTier property.
/// </summary>
file sealed class TierSettingStream(Stream inner, BlockBlobClient blobClient, AccessTier tier)
    : Stream
{
    private bool _disposed;

    public override bool CanWrite => inner.CanWrite;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer)            => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => inner.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => inner.WriteAsync(buffer, ct);
    public override void Flush()                      => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Commit the block list by disposing the inner stream
            inner.Dispose();
            // Set the tier synchronously (best-effort; callers in async context should use DisposeAsync)
            blobClient.SetAccessTier(tier);
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await inner.DisposeAsync();
            await blobClient.SetAccessTierAsync(tier);
            _disposed = true;
        }
        await base.DisposeAsync();
    }

    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();
}
