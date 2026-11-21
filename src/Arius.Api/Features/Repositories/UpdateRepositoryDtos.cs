namespace Arius.Api.Features.Repositories;

/// <summary>Null fields are left unchanged, so secrets need not be supplied again.</summary>
/// <param name="Alias">New display name.</param>
/// <param name="LocalPath">New folder on the server to archive and restore to.</param>
/// <param name="Passphrase">New passphrase.</param>
public sealed record UpdateRepositoryRequest(
    string? Alias,
    string? LocalPath,
    string? Passphrase);
