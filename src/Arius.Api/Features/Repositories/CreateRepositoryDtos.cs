namespace Arius.Api.Features.Repositories;

/// <summary>A repository to add.</summary>
/// <param name="AccountId">ID of a saved storage account.</param>
/// <param name="Container">Blob container name. The first archive creates the container if it does not exist.</param>
/// <param name="Alias">Display name.</param>
/// <param name="Passphrase">Passphrase that encrypts the repository content, or <c>null</c> to store it unencrypted.</param>
/// <param name="LocalPath">Folder on the server to archive and restore to.</param>
public sealed record CreateRepositoryRequest(
    long    AccountId,
    string  Container,
    string  Alias,
    string? Passphrase,
    string? LocalPath);
