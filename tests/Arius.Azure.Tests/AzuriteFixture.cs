using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace Arius.Azure.Tests;

/// <summary>
/// TUnit class-level fixture that starts a single Azurite container for all
/// Azure integration tests in this session.
/// </summary>
public sealed class AzuriteFixture : IAsyncDisposable
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.33.0").Build();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private BlobServiceClient? _blobServiceClient;

    /// <summary>
    /// Returns a <see cref="BlobServiceClient"/> connected to the Azurite emulator,
    /// starting the container on the first call.
    /// </summary>
    public async Task<BlobServiceClient> GetBlobServiceClientAsync()
    {
        if (_blobServiceClient is not null)
            return _blobServiceClient;

        await _startLock.WaitAsync();
        try
        {
            if (_blobServiceClient is not null)
                return _blobServiceClient;

            await _azurite.StartAsync();
            _blobServiceClient = new BlobServiceClient(_azurite.GetConnectionString());
            return _blobServiceClient;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _azurite.StopAsync();
        await _azurite.DisposeAsync();
        _startLock.Dispose();
    }
}
