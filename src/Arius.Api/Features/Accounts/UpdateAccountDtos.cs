namespace Arius.Api.Features.Accounts;

/// <summary>Changes to a storage account.</summary>
/// <param name="AccountKey">New storage account access key, or <c>null</c> to keep the stored key.</param>
public sealed record UpdateAccountRequest(string? AccountKey);
