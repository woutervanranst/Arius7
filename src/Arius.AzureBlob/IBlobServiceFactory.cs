using Arius.Core.Storage;

namespace Arius.AzureBlob;

public interface IBlobServiceFactory
{
    Task<IBlobService> CreateAsync(
        string accountName,
        string? accountKey,
        CancellationToken cancellationToken = default);
}
