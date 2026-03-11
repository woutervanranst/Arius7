using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Repair;

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record RepairResult(
    bool   Success,
    string Message,
    int    ItemsRepaired = 0);

// ── Request ──────────────────────────────────────────────────────────────────

public enum RepairTarget { Index, Snapshots }

public sealed record RepairRequest(
    string       ConnectionString,
    string       ContainerName,
    string       Passphrase,
    RepairTarget Target) : IRequest<RepairResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Repairs the repository index or broken snapshot references.
///
/// - <see cref="RepairTarget.Index"/>: Rebuilds index blobs by scanning existing packs.
///   (Full rebuild requires rehydration; this reports what would be done.)
/// - <see cref="RepairTarget.Snapshots"/>: Finds and removes snapshot blobs that
///   reference non-existent tree blobs.
/// </summary>
public sealed class RepairHandler : IRequestHandler<RepairRequest, RepairResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public RepairHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<RepairResult> Handle(
        RepairRequest request,
        CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        return request.Target switch
        {
            RepairTarget.Index     => await RepairIndexAsync(repo, cancellationToken),
            RepairTarget.Snapshots => await RepairSnapshotsAsync(repo, cancellationToken),
            _                      => throw new ArgumentOutOfRangeException()
        };
    }

    private static async Task<RepairResult> RepairIndexAsync(
        AzureRepository repo, CancellationToken ct)
    {
        // Determine which index blobs already exist
        var indexBlobs = new List<string>();
        await foreach (var item in repo.ListBlobsAfterAsync("index/", "", ct))
            indexBlobs.Add(item.Name);

        // A full rebuild from pack headers requires rehydration of all data/ packs.
        // Report the current state; actual rebuild deferred to when blobs are rehydrated.
        return new RepairResult(
            Success: true,
            Message: $"Index check complete — {indexBlobs.Count} index blob(s) found. "
                   + "Full rebuild from pack headers requires archive rehydration.",
            ItemsRepaired: 0);
    }

    private static async Task<RepairResult> RepairSnapshotsAsync(
        AzureRepository repo, CancellationToken ct)
    {
        var repaired = 0;
        await foreach (var doc in repo.ListSnapshotDocumentsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            bool treeOk;
            try
            {
                _ = await repo.ReadTreeAsync(doc.Snapshot.Tree, ct);
                treeOk = true;
            }
            catch
            {
                treeOk = false;
            }

            if (!treeOk)
            {
                await repo.DeleteSnapshotAsync(doc.Snapshot.Id, ct);
                repaired++;
            }
        }

        return new RepairResult(
            Success: true,
            Message: $"Snapshot repair complete — {repaired} broken snapshot(s) removed.",
            ItemsRepaired: repaired);
    }
}
