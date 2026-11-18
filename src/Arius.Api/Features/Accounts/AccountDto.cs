namespace Arius.Api.Contracts;

/// <summary>A storage account as shown to the client. The account key is never returned.</summary>
public sealed record AccountDto(long Id, string Name, int Repositories, bool HasKey);
