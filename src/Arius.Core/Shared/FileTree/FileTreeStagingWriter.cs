using Arius.Core.Shared.Hashes;

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
        string filePath,
        ContentHash contentHash,
        DateTimeOffset created,
        DateTimeOffset modified,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateCanonicalRelativePath(filePath);

        var segments = filePath.Split('/');
        if (segments.Length == 0)
            throw new ArgumentException("File path must include a file name.", nameof(filePath));

        var fileName = segments[^1];
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File path must include a non-empty file name.", nameof(filePath));

        var parentPath = segments.Length == 1 ? string.Empty : string.Join('/', segments, 0, segments.Length - 1);

        await AppendDirectoryEntriesAsync(segments, cancellationToken);
        await AppendFileEntryAsync(parentPath, new FileEntry
        {
            Name        = fileName,
            ContentHash = contentHash,
            Created     = created,
            Modified    = modified
        }, cancellationToken);

        static void ValidateCanonicalRelativePath(string path)
        {
            if (path.StartsWith('/') || Path.IsPathRooted(path))
                throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

            var segments = path.Split('/');
            if (segments.Length == 0)
                throw new ArgumentException("File path must include a file name.", nameof(filePath));

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || string.IsNullOrWhiteSpace(segment))
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

                if (segment is "." or "..")
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

                if (segment.Contains('\\'))
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));
            }
        }
    }

    private async Task AppendFileEntryAsync(string directoryPath, FileEntry entry, CancellationToken cancellationToken)
    {
        var directoryId = FileTreePaths.GetStagingDirectoryId(directoryPath);
        var nodePath = FileTreePaths.GetStagingNodePath(_stagingRoot, directoryId);
        await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedFileEntryLine(entry), cancellationToken);
    }

    private async Task AppendDirectoryEntriesAsync(string[] segments, CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < segments.Length - 1; depth++)
        {
            var parentPath    = depth == 0 ? string.Empty : string.Join('/', segments, 0, depth);
            var directoryPath = string.Join('/', segments, 0, depth + 1);
            var directoryName = segments[depth] + "/";
            var directoryId   = FileTreePaths.GetStagingDirectoryId(directoryPath);
            var nodePath      = FileTreePaths.GetStagingNodePath(_stagingRoot, FileTreePaths.GetStagingDirectoryId(parentPath));

            await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedDirectoryEntryLine(directoryId, directoryName), cancellationToken);
        }
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
