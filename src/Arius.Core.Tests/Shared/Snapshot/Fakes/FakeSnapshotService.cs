using Arius.Core.Shared.Snapshot;

namespace Arius.Core.Tests.Shared.Snapshot.Fakes;

internal sealed class FakeSnapshotService : ISnapshotService
{
    private readonly IReadOnlyList<RelativePath> _blobNames;

    public FakeSnapshotService() => _blobNames = [];
    public FakeSnapshotService(IReadOnlyList<RelativePath> blobNames) => _blobNames = blobNames;

    public int ListBlobNamesCallCount { get; private set; }

    public Task<SnapshotManifest> CreateAsync(
        FileTreeHash      rootHash,
        long              fileCount,
        long              originalSize,
        DateTimeOffset?   timestamp         = null,
        bool              overwrite         = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RelativePath>> ListBlobNamesAsync(CancellationToken cancellationToken = default)
    {
        ListBlobNamesCallCount++;
        return Task.FromResult(_blobNames);
    }

    public Task<SnapshotManifest?> ResolveAsync(string? version = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public string GetVersion(RelativePath blobName) => blobName.Name.ToString();
}
