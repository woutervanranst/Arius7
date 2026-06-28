using System.Collections.Concurrent;

namespace Arius.Core.Shared.FileTree;

[SharedWithinAssembly]
internal sealed class FileTreeStagingWriter : IDisposable
{
    private const int StripeCount = 256; // Note: we used to have a lock for every staging file, but that was unbounded. Now we have bounded memory by striping the locks

    private readonly SemaphoreSlim[] _lockStripes;
    private readonly RelativeFileSystem _stagingFileSystem;
    private          bool            _disposed;

    // A directory id is globally unique to its full path, so its parent→child edge line is identical
    // no matter which descendant file triggers it. Emit each edge once: without this, a deep tree
    // re-appends every ancestor edge for every file (the root node once per file), which both dominates
    // the staging I/O and funnels all writers onto the root node's single stripe lock. The reader
    // (FileTreeBuilder.ReadNodeEntriesAsync) already collapses duplicate directory entries, so writing
    // each once is behaviourally identical. Bounded by directory count and released with the writer.
    private readonly ConcurrentDictionary<PathSegment, bool> _emittedDirectories = new();

    public FileTreeStagingWriter(LocalDirectory stagingRoot)
    {
        _stagingFileSystem = new RelativeFileSystem(stagingRoot);
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
        if (filePath == RelativePath.Root)
            throw new InvalidOperationException("File path must include a file name.");

        cancellationToken.ThrowIfCancellationRequested();

        await AppendDirectoryEntriesAsync(filePath, cancellationToken);
        await AppendFileEntryAsync(filePath.Parent ?? RelativePath.Root, new FileEntry
        {
            Name        = filePath.Name,
            ContentHash = contentHash,
            Created     = created,
            Modified    = modified
        }, cancellationToken);
    }

    private async Task AppendFileEntryAsync(RelativePath directoryPath, FileEntry entry, CancellationToken cancellationToken)
    {
        var directoryId = FileTreePaths.GetStagingDirectoryId(directoryPath);
        var nodePath = FileTreePaths.GetStagingNodePath(directoryId);
        await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedFileEntryLine(entry), cancellationToken);
    }

    private async Task AppendDirectoryEntriesAsync(RelativePath filePath, CancellationToken cancellationToken)
    {
        var currentPath = RelativePath.Root;

        foreach (var segment in filePath.Segments.Take(filePath.Segments.Count() - 1))
        {
            var parentPath = currentPath;
            currentPath = currentPath / segment;
            var directoryId = FileTreePaths.GetStagingDirectoryId(currentPath);

            // Skip the append (but keep descending) when this directory edge was already emitted.
            // TryAdd is atomic: exactly one writer wins and writes the edge.
            if (!_emittedDirectories.TryAdd(directoryId, true))
                continue;

            var nodePath = FileTreePaths.GetStagingNodePath(FileTreePaths.GetStagingDirectoryId(parentPath));

            await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedDirectoryEntryLine(directoryId.ToString(), segment), cancellationToken);
        }
    }

    private async Task AppendLineAsync(RelativePath path, string line, CancellationToken cancellationToken)
    {
        var nodeLock = _lockStripes[(uint)StringComparer.Ordinal.GetHashCode(path) % (uint)_lockStripes.Length];
        await nodeLock.WaitAsync(cancellationToken);

        try
        {
            // Fixed '\n' (not Environment.NewLine): staged lines are re-serialized by FileTreeSerializer
            // before hashing, but keep the staging format platform-independent and consistent with it.
            await _stagingFileSystem.AppendAllTextAsync(path, line + "\n", cancellationToken);
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
