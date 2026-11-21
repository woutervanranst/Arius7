namespace Arius.Api.Features.Accounts;

/// <summary>A storage account to add.</summary>
/// <param name="Name">Storage account name.</param>
/// <param name="AccountKey">Storage account access key. Omit it to let the server sign in with its Azure CLI credential.</param>
public sealed record CreateAccountRequest(string Name, string? AccountKey);
