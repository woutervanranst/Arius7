using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;

namespace Arius.Core.Application.Backup;

public sealed class BackupHandler : IStreamRequestHandler<BackupRequest, BackupEvent>
{
    private readonly FileSystemRepositoryStore _repositoryStore = new();

    public async IAsyncEnumerable<BackupEvent> Handle(BackupRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var files = ExpandFiles(request.Paths).ToList();
        yield return new BackupStarted(files.Count);

        var passphraseIsValid = await _repositoryStore.ValidatePassphraseAsync(request.RepoPath, request.Passphrase, cancellationToken);
        if (!passphraseIsValid)
        {
            throw new InvalidOperationException("Invalid repository passphrase.");
        }

        var (snapshot, stored, deduplicated) = await _repositoryStore.BackupAsync(request.RepoPath, files, cancellationToken);

        var deduplicatedLeft = deduplicated;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var isDeduplicated = deduplicatedLeft > 0;
            if (isDeduplicated)
            {
                deduplicatedLeft--;
            }

            yield return new BackupFileProcessed(info.FullName, info.Length, isDeduplicated);
        }

        yield return new BackupCompleted(snapshot, stored, deduplicated);
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
                {
                    yield return Path.GetFullPath(file);
                }
            }
        }
    }
}
