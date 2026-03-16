using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

public sealed class RestoreHandler : IStreamRequestHandler<RestoreRequest, RestoreEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public RestoreHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<RestoreEvent> Handle(
        RestoreRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo      = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        // Load snapshot
        var doc   = await repo.LoadSnapshotDocumentAsync(request.SnapshotId, cancellationToken);
        var files = (IReadOnlyList<BackupSnapshotFile>)doc.Files;

        if (!string.IsNullOrEmpty(request.Include))
        {
            files = files
                .Where(f => f.Path.Contains(request.Include, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Load index once
        var index = await repo.LoadIndexAsync(cancellationToken);

        var opts    = (request.Parallelism ?? ParallelismOptions.Default).Resolve();
        var tempDir = request.TempPath
            ?? Path.Combine(Path.GetTempPath(), "arius-restore", Guid.NewGuid().ToString("N"));

        var pipeline = new RestorePipeline(repo, masterKey, opts, request.TargetPath, tempDir);

        await foreach (var evt in pipeline.RunAsync(files, index, cancellationToken))
            yield return evt;
    }
}
