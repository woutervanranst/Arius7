namespace Arius.Api.Contracts;

public sealed record CreateRepositoryRequest(
    long    AccountId,
    string  Container,
    string  Alias,
    string? Passphrase,
    string? LocalPath);
