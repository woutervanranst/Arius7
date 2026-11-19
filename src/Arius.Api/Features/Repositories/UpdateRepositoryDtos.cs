namespace Arius.Api.Features.Repositories;

/// <summary>Null fields are left unchanged, so secrets need not be supplied again.</summary>
public sealed record UpdateRepositoryRequest(
    string? Alias,
    string? LocalPath,
    string? Passphrase);
