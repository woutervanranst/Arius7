using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Snapshots;

public sealed record ListSnapshotsRequest(string RepoPath, string Passphrase) : IStreamRequest<Snapshot>;

public sealed class SnapshotsHandler : IStreamRequestHandler<ListSnapshotsRequest, Snapshot>
{
    private readonly FileSystemRepositoryStore _repositoryStore = new();

    public async IAsyncEnumerable<Snapshot> Handle(
        ListSnapshotsRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var passphraseIsValid = await _repositoryStore.ValidatePassphraseAsync(
            request.RepoPath, request.Passphrase, cancellationToken);

        if (!passphraseIsValid)
            throw new InvalidOperationException("Invalid repository passphrase.");

        await foreach (var snapshot in _repositoryStore.ListSnapshotsAsync(request.RepoPath, cancellationToken))
        {
            yield return snapshot;
        }
    }
}
