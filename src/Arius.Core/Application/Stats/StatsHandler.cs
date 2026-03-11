using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Stats;

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record RepoStats(
    int    SnapshotCount,
    int    PackCount,
    long   TotalPackBytes,
    int    UniqueBlobCount,
    long   UniqueBlobBytes,
    double DeduplicationRatio);

// ── Request ──────────────────────────────────────────────────────────────────

public sealed record StatsRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase) : IRequest<RepoStats>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class StatsHandler : IRequestHandler<StatsRequest, RepoStats>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public StatsHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<RepoStats> Handle(
        StatsRequest request,
        CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        var index    = await repo.LoadIndexAsync(cancellationToken);
        var packIds  = index.Values.Select(e => e.PackId.Value).Distinct().ToList();
        var totalPackBytes = index.Values.Sum(e => e.Length);
        var uniqueBytes    = index.Values
            .GroupBy(e => e.BlobHash.Value)
            .Sum(g => g.First().Length);

        var snapshotCount = 0;
        await foreach (var _ in repo.ListSnapshotDocumentsAsync(cancellationToken))
            snapshotCount++;

        var dedup = uniqueBytes > 0
            ? totalPackBytes / (double)uniqueBytes
            : 1.0;

        return new RepoStats(
            SnapshotCount:     snapshotCount,
            PackCount:         packIds.Count,
            TotalPackBytes:    totalPackBytes,
            UniqueBlobCount:   index.Count,
            UniqueBlobBytes:   uniqueBytes,
            DeduplicationRatio: dedup);
    }
}
