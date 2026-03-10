using System.Text.Json;
using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

public sealed class RestoreHandler : IStreamRequestHandler<RestoreRequest, RestoreEvent>
{
    private readonly FileSystemRepositoryStore _repositoryStore = new();

    public async IAsyncEnumerable<RestoreEvent> Handle(
        RestoreRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var passphraseIsValid = await _repositoryStore.ValidatePassphraseAsync(
            request.RepoPath, request.Passphrase, cancellationToken);

        if (!passphraseIsValid)
            throw new InvalidOperationException("Invalid repository passphrase.");

        var (files, totalBytes) = await _repositoryStore.PlanRestoreAsync(
            request.RepoPath, request.SnapshotId, request.Include, cancellationToken);

        yield return new RestorePlanReady(files.Count, totalBytes);

        Directory.CreateDirectory(request.TargetPath);

        long restoredBytes = 0;
        int restoredFiles = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _repositoryStore.RestoreFileAsync(
                request.RepoPath, file, request.TargetPath, cancellationToken);

            restoredFiles++;
            restoredBytes += file.Size;
            yield return new RestoreFileRestored(file.Path, file.Size);
        }

        yield return new RestoreCompleted(restoredFiles, restoredBytes);
    }
}
