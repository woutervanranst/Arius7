using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Prune;

// ── Events ─────────────────────────────────────────────────────────────────

public enum PruneEventKind
{
    Analysing,
    WillDelete,
    WillRepack,
    Deleting,
    Repacking,
    Done
}

public sealed record PruneEvent(
    PruneEventKind Kind,
    string         Message,
    string?        PackId = null,
    long           BytesAffected = 0);

// ── Request ─────────────────────────────────────────────────────────────────

public sealed record PruneRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    bool   DryRun  = false,
    int    MinAgeDays = 0) : IStreamRequest<PruneEvent>;

// ── Handler ──────────────────────────────────────────────────────────────────

/// <summary>
/// Identifies unreferenced packs and optionally repacks partially-used packs.
///
/// Algorithm:
///  1. Load full index → build set of referenced PackIds.
///  2. List all data/ blobs.
///  3. For each pack: if all its indexed blobs are referenced ⟶ keep;
///     if none ⟶ fully delete; if some ⟶ repack (extract referenced blobs, create new pack, update index, delete old).
/// </summary>
public sealed class PruneHandler : IStreamRequestHandler<PruneRequest, PruneEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public PruneHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<PruneEvent> Handle(
        PruneRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo      = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        yield return new PruneEvent(PruneEventKind.Analysing, "Loading index and snapshots…");

        // 1. Build the set of referenced blob hashes from all snapshots
        var referencedBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
        {
            await CollectBlobsFromTree(repo, doc.Snapshot.Tree, referencedBlobs, cancellationToken);
        }

        // 2. Load full index: blobHash → IndexEntry
        var index = await repo.LoadIndexAsync(cancellationToken);

        // 3. Group index entries by pack
        var packToEntries = index.Values
            .GroupBy(e => e.PackId.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 4. Analyse packs
        var toDelete  = new List<string>();
        var toRepack  = new List<string>();

        foreach (var (packId, entries) in packToEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var referenced   = entries.Count(e => referencedBlobs.Contains(e.BlobHash.Value));
            var total        = entries.Count;

            if (referenced == 0)
            {
                toDelete.Add(packId);
                yield return new PruneEvent(PruneEventKind.WillDelete,
                    $"Pack {packId[..8]} — fully unreferenced ({total} blobs)", packId);
            }
            else if (referenced < total)
            {
                toRepack.Add(packId);
                yield return new PruneEvent(PruneEventKind.WillRepack,
                    $"Pack {packId[..8]} — {referenced}/{total} blobs referenced", packId);
            }
        }

        if (request.DryRun)
        {
            yield return new PruneEvent(PruneEventKind.Done,
                $"Dry run complete. {toDelete.Count} to delete, {toRepack.Count} to repack.");
            yield break;
        }

        // 5. Delete fully-unreferenced packs
        foreach (var packId in toDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await repo.DeletePackAsync(new PackId(packId), cancellationToken);
            yield return new PruneEvent(PruneEventKind.Deleting,
                $"Deleted pack {packId[..8]}", packId);
        }

        // 6. Repack partially-used packs
        // (repacking requires rehydration; for now we emit events and skip actual rehydration
        //  — full repack with rehydration can be added when RestoreHandler supports it)
        foreach (var packId in toRepack)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new PruneEvent(PruneEventKind.Repacking,
                $"Repack of {packId[..8]} requires rehydration — skipped (run 'prune --rehydrate' when blobs are hot)",
                packId);
        }

        yield return new PruneEvent(PruneEventKind.Done,
            $"Prune complete. Deleted {toDelete.Count} pack(s). {toRepack.Count} pack(s) need rehydration for repacking.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task CollectBlobsFromTree(
        AzureRepository repo,
        TreeHash treeHash,
        HashSet<string> blobs,
        CancellationToken ct)
    {
        var nodes = await repo.ReadTreeAsync(treeHash, ct);
        foreach (var node in nodes)
        {
            foreach (var bh in node.ContentHashes)
                blobs.Add(bh.Value);

            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } subHash)
                await CollectBlobsFromTree(repo, subHash, blobs, ct);
        }
    }
}
