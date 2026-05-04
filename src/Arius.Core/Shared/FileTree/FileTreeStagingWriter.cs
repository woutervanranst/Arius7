using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared.FileTree;

internal sealed class FileTreeStagingWriter : IDisposable
{
    private const int StripeCount = 256; // Note: we used to have a lock for every staging file, but that was unbounded. Now we have bounded memory by striping the locks

    private readonly SemaphoreSlim[] _lockStripes;
    private readonly string          _stagingRoot;
    private          bool            _disposed;

    public FileTreeStagingWriter(string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = stagingRoot;
        _lockStripes = Enumerable.Range(0, StripeCount)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public async Task AppendFileEntryAsync(
        RelativePath filePath,
        ContentHash contentHash,
        DateTimeOffset created,
        DateTimeOffset modified,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fileName = filePath.Name
            ?? throw new ArgumentException("File path must include a file name.", nameof(filePath));

        var parentPath = filePath.Parent ?? RelativePath.Root;

        await AppendDirectoryEntriesAsync(parentPath, cancellationToken);
        await AppendFileEntryAsync(parentPath, new FileEntry
        {
            Name        = fileName,
            ContentHash = contentHash,
            Created     = created,
            Modified    = modified
        }, cancellationToken);
    }

    private async Task AppendFileEntryAsync(RelativePath directoryPath, FileEntry entry, CancellationToken cancellationToken)
    {
        var directoryId = FileTreePaths.GetStagingDirectoryId(directoryPath);
        var nodePath = FileTreePaths.GetStagingNodePath(_stagingRoot, directoryId);
        await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedFileEntryLine(entry), cancellationToken);
    }

    private async Task AppendDirectoryEntriesAsync(RelativePath directoryPath, CancellationToken cancellationToken)
    {
        if (directoryPath.IsRoot)
            return;

        var parentPath = directoryPath.Parent ?? RelativePath.Root;
        await AppendDirectoryEntriesAsync(parentPath, cancellationToken);

        var directoryName = directoryPath.Name
            ?? throw new InvalidOperationException("Directory path must include a directory name.");
        var directoryId = FileTreePaths.GetStagingDirectoryId(directoryPath);
        var nodePath = FileTreePaths.GetStagingNodePath(_stagingRoot, FileTreePaths.GetStagingDirectoryId(parentPath));

        await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedDirectoryEntryLine(directoryId, directoryName), cancellationToken);
    }

    private async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        var nodeLock = _lockStripes[(uint)StringComparer.Ordinal.GetHashCode(path) % (uint)_lockStripes.Length];
        await nodeLock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(_stagingRoot);

            await File.AppendAllLinesAsync(path, [line], cancellationToken);
        }
        finally
        {
            nodeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var nodeLock in _lockStripes)
            nodeLock.Dispose();
    }
}
