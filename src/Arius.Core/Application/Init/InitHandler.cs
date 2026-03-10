using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;

namespace Arius.Core.Application.Init;

public sealed class InitHandler : IRequestHandler<InitRequest, InitResult>
{
    private readonly FileSystemRepositoryStore _repositoryStore = new();

    public async ValueTask<InitResult> Handle(InitRequest request, CancellationToken cancellationToken = default)
    {
        var initResult = await _repositoryStore.InitAsync(
            request.RepoPath,
            request.Passphrase,
            request.PackSize,
            request.ChunkMin,
            request.ChunkAvg,
            request.ChunkMax,
            cancellationToken);

        return new InitResult(initResult.RepoId, initResult.ConfigPath, initResult.KeyPath);
    }
}
