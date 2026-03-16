using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Models;

namespace Arius.Core.Application.Backup;

public sealed class BackupHandler : IStreamRequestHandler<BackupRequest, BackupEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public BackupHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<BackupEvent> Handle(
        BackupRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo      = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);
        var config    = await repo.LoadConfigAsync(cancellationToken);
        var chunker   = GearChunker.FromConfig(config);

        var files = ExpandFiles(request.Paths).ToList();
        yield return new BackupStarted(files.Count);

        // Load existing index for dedup (now loads concurrently)
        var existingIndex = await repo.LoadIndexAsync(cancellationToken);

        var opts = (request.Parallelism ?? ParallelismOptions.Default).Resolve();

        var pipeline = new BackupPipeline(
            repo, masterKey, config, chunker, existingIndex,
            request.DataTier, opts, request.Paths);

        await foreach (var evt in pipeline.RunAsync(files, cancellationToken))
            yield return evt;
    }

    private static IEnumerable<string> ExpandFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    yield return Path.GetFullPath(file);
            }
        }
    }
}
