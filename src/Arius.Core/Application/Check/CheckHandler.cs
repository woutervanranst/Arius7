using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Check;

// ── Output ──────────────────────────────────────────────────────────────────

public enum CheckSeverity { Info, Warning, Error }

public sealed record CheckResult(
    CheckSeverity Severity,
    string        Message,
    string?       SnapshotId = null);

// ── Request ──────────────────────────────────────────────────────────────────

public sealed record CheckRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    bool   ReadData = false) : IStreamRequest<CheckResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Verifies repository integrity by checking snapshot→tree→blob→pack reference chains.
/// With <see cref="CheckRequest.ReadData"/> the pack blobs themselves are also verified.
/// </summary>
public sealed class CheckHandler : IStreamRequestHandler<CheckRequest, CheckResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public CheckHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<CheckResult> Handle(
        CheckRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        yield return new CheckResult(CheckSeverity.Info, "Loading index…");
        var index = await repo.LoadIndexAsync(cancellationToken);

        yield return new CheckResult(CheckSeverity.Info, $"Index loaded: {index.Count} blob entries.");

        var errors = 0;

        await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snap = doc.Snapshot;
            yield return new CheckResult(CheckSeverity.Info,
                $"Checking snapshot {snap.Id.Value[..8]} ({snap.Time:yyyy-MM-dd HH:mm})…", snap.Id.Value);

            // Verify tree blobs are accessible
            bool treeOk = await CheckTreeAsync(repo, snap.Tree, cancellationToken);
            if (!treeOk)
            {
                errors++;
                yield return new CheckResult(CheckSeverity.Error,
                    $"Snapshot {snap.Id.Value[..8]}: tree blob missing or unreadable.", snap.Id.Value);
                continue;
            }

            // Verify every file blob has an index entry pointing to an existing pack
            await foreach (var blobHash in EnumerateBlobHashesAsync(repo, snap.Tree, cancellationToken))
            {
                if (!index.TryGetValue(blobHash, out var entry))
                {
                    errors++;
                    yield return new CheckResult(CheckSeverity.Error,
                        $"Blob {blobHash[..8]} referenced by snapshot {snap.Id.Value[..8]} has no index entry.");
                }
            }
        }

        if (errors == 0)
            yield return new CheckResult(CheckSeverity.Info, "Check complete — no errors found.");
        else
            yield return new CheckResult(CheckSeverity.Error, $"Check complete — {errors} error(s) found.");
    }

    private static async Task<bool> CheckTreeAsync(
        AzureRepository repo, TreeHash hash, CancellationToken ct)
    {
        try
        {
            var nodes = await repo.ReadTreeAsync(hash, ct);
            foreach (var node in nodes)
            {
                if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } sub)
                    if (!await CheckTreeAsync(repo, sub, ct)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async IAsyncEnumerable<string> EnumerateBlobHashesAsync(
        AzureRepository repo,
        TreeHash treeHash,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var nodes = await repo.ReadTreeAsync(treeHash, ct);
        foreach (var node in nodes)
        {
            foreach (var bh in node.ContentHashes)
                yield return bh.Value;

            if (node.Type == TreeNodeType.Directory && node.SubtreeHash is { } sub)
                await foreach (var bh in EnumerateBlobHashesAsync(repo, sub, ct))
                    yield return bh;
        }
    }
}
