using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;

namespace Arius.Core.Application.Init;

public sealed class InitHandler : IRequestHandler<InitRequest, InitResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public InitHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<InitResult> Handle(InitRequest request, CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);

        var (repoId, configBlobName, keyBlobName) = await repo.InitAsync(
            request.Passphrase,
            request.PackSize,
            request.ChunkMin,
            request.ChunkAvg,
            request.ChunkMax,
            cancellationToken);

        return new InitResult(repoId, configBlobName, keyBlobName);
    }
}
