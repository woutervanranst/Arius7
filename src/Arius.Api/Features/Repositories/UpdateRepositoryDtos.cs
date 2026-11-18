namespace Arius.Api.Contracts;

/// <summary>Properties-screen update. Null fields are left unchanged (so secrets need not be resupplied).</summary>
public sealed record UpdateRepositoryRequest(
    string? Alias,
    string? LocalPath,
    string? Passphrase);
