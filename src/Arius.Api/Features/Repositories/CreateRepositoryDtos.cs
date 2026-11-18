namespace Arius.Api.Features.Repositories;

public sealed record CreateRepositoryRequest(
    long    AccountId,
    string  Container,
    string  Alias,
    string? Passphrase,
    string? LocalPath);
