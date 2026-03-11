using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Application.Init;

public sealed record InitRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    long PackSize = 10 * 1024 * 1024,
    int ChunkMin = 256 * 1024,
    int ChunkAvg = 1024 * 1024,
    int ChunkMax = 4 * 1024 * 1024) : IRequest<InitResult>;

public sealed record InitResult(RepoId RepoId, string ConfigBlobName, string KeyBlobName);
