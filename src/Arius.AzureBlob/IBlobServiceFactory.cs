using Arius.Core.Storage;

namespace Arius.AzureBlob;

/// <summary>
/// Creates account-scoped blob services from an account name and optional shared key.
/// When <paramref name="accountKey"/> is null or whitespace, Azure CLI authentication is used.
/// </summary>
public interface IBlobServiceFactory
{
    Task<IBlobService> CreateAsync(
        string accountName,
        string? accountKey,
        CancellationToken cancellationToken = default);
}
