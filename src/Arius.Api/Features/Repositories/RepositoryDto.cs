namespace Arius.Api.Features.Repositories;

/// <summary>A repository as shown to the client. Secrets (key, passphrase) are never returned.</summary>
/// <param name="Id">Repository ID.</param>
/// <param name="Alias">Display name.</param>
/// <param name="Container">Blob container that holds the repository.</param>
/// <param name="AccountId">ID of the storage account.</param>
/// <param name="Account">Name of the storage account.</param>
/// <param name="LocalPath">Folder on the server that is archived and restored to, or <c>null</c> if not set.</param>
/// <param name="DefaultTier">Storage tier that archives use (<c>hot</c>, <c>cool</c>, <c>cold</c> or <c>archive</c>), resolved from the defaults.</param>
/// <param name="Region">Region that storage costs are priced for, or <c>null</c> if it is not known.</param>
/// <param name="RegionIsDefault">Whether <c>region</c> is the storage provider's fallback because the container has no region set; set it for accurate pricing.</param>
/// <param name="LastArchiveStartedAt">Start time of the most recent archive job, or <c>null</c> if none has run.</param>
public sealed record RepositoryDto(
    long    Id,
    string  Alias,
    string  Container,
    long    AccountId,
    string  Account,
    string? LocalPath,
    string  DefaultTier,
    string? Region,
    bool    RegionIsDefault,
    DateTimeOffset? LastArchiveStartedAt = null);
