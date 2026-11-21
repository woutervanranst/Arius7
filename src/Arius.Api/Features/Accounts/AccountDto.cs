namespace Arius.Api.Features.Accounts;

/// <summary>A storage account as shown to the client. The account key is never returned.</summary>
/// <param name="Id">Account ID.</param>
/// <param name="Name">Storage account name.</param>
/// <param name="Repositories">Number of repositories that use this account.</param>
/// <param name="HasKey">Whether an account key is stored. Without a key, the server signs in with its Azure CLI credential.</param>
public sealed record AccountDto(long Id, string Name, int Repositories, bool HasKey);
