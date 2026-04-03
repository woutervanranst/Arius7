namespace Arius.Core.Shared.Storage;

/// <summary>
/// Creates account-scoped blob services from an account name and optional shared key.
/// </summary>
public interface IBlobServiceFactory
{
    Task<IBlobService> CreateAsync(
        string accountName,
        string? accountKey,
        CancellationToken cancellationToken = default);
}
