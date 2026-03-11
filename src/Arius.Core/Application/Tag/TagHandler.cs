using System.Text.Json;
using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Tag;

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record TagResult(
    string                 SnapshotId,
    IReadOnlyList<string>  Tags,
    string                 Message);

// ── Request ──────────────────────────────────────────────────────────────────

public enum TagOperation { Add, Remove, Set }

public sealed record TagRequest(
    string                ConnectionString,
    string                ContainerName,
    string                Passphrase,
    string                SnapshotId,
    TagOperation          Operation,
    IReadOnlyList<string> Tags) : IRequest<TagResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class TagHandler : IRequestHandler<TagRequest, TagResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public TagHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<TagResult> Handle(
        TagRequest request,
        CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        var doc = await repo.LoadSnapshotDocumentAsync(request.SnapshotId, cancellationToken);

        var existingTags = doc.Snapshot.Tags.ToList();
        List<string> newTags = request.Operation switch
        {
            TagOperation.Set    => request.Tags.ToList(),
            TagOperation.Add    => existingTags.Union(request.Tags, StringComparer.OrdinalIgnoreCase).ToList(),
            TagOperation.Remove => existingTags.Except(request.Tags, StringComparer.OrdinalIgnoreCase).ToList(),
            _                   => throw new ArgumentOutOfRangeException()
        };

        var updatedSnapshot = doc.Snapshot with { Tags = newTags };
        var updatedDoc      = doc with { Snapshot = updatedSnapshot };
        await repo.WriteSnapshotAsync(updatedDoc, cancellationToken);

        return new TagResult(
            SnapshotId: doc.Snapshot.Id.Value,
            Tags:       newTags,
            Message:    $"Tags updated on snapshot {doc.Snapshot.Id.Value[..8]}.");
    }
}
