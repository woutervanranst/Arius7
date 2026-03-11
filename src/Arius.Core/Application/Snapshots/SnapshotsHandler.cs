using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Snapshots;

public sealed record ListSnapshotsRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase) : IStreamRequest<Snapshot>;

public sealed class SnapshotsHandler : IStreamRequestHandler<ListSnapshotsRequest, Snapshot>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public SnapshotsHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<Snapshot> Handle(
        ListSnapshotsRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);

        // Validate passphrase before listing
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
            yield return doc.Snapshot;
    }
}
