namespace Arius.Api.Contracts;

/// <summary>A repository as shown to the client. Secrets (key, passphrase) are never returned.</summary>
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
