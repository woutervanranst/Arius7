using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.CostEstimate;

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record RestoreCostEstimate(
    long   TotalBytes,
    int    PackCount,
    double EstimatedRehydrationCostUsd,
    double EstimatedEarlyDeletionCostUsd,
    string Notes);

// ── Request ──────────────────────────────────────────────────────────────────

public sealed record CostEstimateRequest(
    string  ConnectionString,
    string  ContainerName,
    string  Passphrase,
    string? SnapshotId = null) : IRequest<RestoreCostEstimate>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Estimates the cost of restoring a snapshot (or the full repository)
/// from Azure Archive tier.
///
/// Pricing assumptions (approximations — actual cost depends on region/negotiated rates):
///   - Archive rehydration (standard priority): $0.02 / GB
///   - Early deletion fee: $0.002 / GB (if pack is &lt; 180 days old)
/// </summary>
public sealed class CostEstimateHandler : IRequestHandler<CostEstimateRequest, RestoreCostEstimate>
{
    // Azure Archive tier approximate prices per GB (USD, approximate)
    private const double RehydrationCostPerGb    = 0.02;
    private const double EarlyDeletionCostPerGb  = 0.002;
    private const int    EarlyDeletionDays        = 180;

    private readonly Func<string, string, AzureRepository> _repoFactory;

    public CostEstimateHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<RestoreCostEstimate> Handle(
        CostEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        // Determine which packs we need for the target snapshot(s)
        var neededPackIds = new HashSet<string>(StringComparer.Ordinal);

        if (request.SnapshotId is not null)
        {
            var doc = await repo.LoadSnapshotDocumentAsync(request.SnapshotId, cancellationToken);
            await CollectRequiredPacksAsync(repo, doc.Snapshot.Tree, neededPackIds, cancellationToken);
        }
        else
        {
            // Estimate for full repository
            await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
                await CollectRequiredPacksAsync(repo, doc.Snapshot.Tree, neededPackIds, cancellationToken);
        }

        // Load index to get pack sizes
        var index      = await repo.LoadIndexAsync(cancellationToken);
        var packSizes  = index.Values
            .Where(e => neededPackIds.Contains(e.PackId.Value))
            .GroupBy(e => e.PackId.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Length));

        long  totalBytes   = packSizes.Values.Sum();
        double totalGb     = totalBytes / (1024.0 * 1024 * 1024);
        double rehydCost   = totalGb * RehydrationCostPerGb;
        double earlyDelCost = totalGb * EarlyDeletionCostPerGb; // conservative: assume all in archive

        return new RestoreCostEstimate(
            TotalBytes:                 totalBytes,
            PackCount:                  packSizes.Count,
            EstimatedRehydrationCostUsd: rehydCost,
            EstimatedEarlyDeletionCostUsd: earlyDelCost,
            Notes: "Archive rehydration uses standard priority. "
                 + $"Early deletion fee applies to packs younger than {EarlyDeletionDays} days. "
                 + "Prices are approximate and vary by region.");
    }

    private static async Task CollectRequiredPacksAsync(
        AzureRepository repo,
        TreeHash treeHash,
        HashSet<string> packIds,
        CancellationToken ct)
    {
        var nodes = await repo.ReadTreeAsync(treeHash, ct);
        foreach (var node in nodes)
        {
            // tree blob hashes are covered by tree storage, not packs
            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } sub)
                await CollectRequiredPacksAsync(repo, sub, packIds, ct);
            // data packs needed for file content — resolved at index lookup time
        }
    }
}
