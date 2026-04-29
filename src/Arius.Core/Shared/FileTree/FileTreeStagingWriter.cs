using System.Collections.Concurrent;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal sealed class FileTreeStagingWriter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new(StringComparer.Ordinal);
    private readonly string _stagingRoot;

    public FileTreeStagingWriter(string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);
        _stagingRoot = stagingRoot;
    }

    public async Task AppendFileAsync(
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
        var parentPath = segments.Length == 1 ? string.Empty : string.Join('/', segments, 0, segments.Length - 1);

        await AppendChildrenAsync(segments, cancellationToken);
        await AppendEntryAsync(parentPath, new FileEntry
        {
            Name = fileName,
            ContentHash = contentHash,
            Created = created,
            Modified = modified
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
                if (segment.Length == 0)
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

                if (segment is "." or "..")
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

                if (segment.Trim() != segment)
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));

                if (segment.Contains('\\'))
                    throw new ArgumentException("File path must be a canonical relative path.", nameof(filePath));
            }
        }
    }

    private async Task AppendEntryAsync(string directoryPath, FileEntry entry, CancellationToken cancellationToken)
    {
        var directoryId = FileTreeStagingPaths.GetDirectoryId(directoryPath);
        var entriesPath = FileTreeStagingPaths.GetEntriesPath(_stagingRoot, directoryId);
        await AppendLineAsync(entriesPath, FileTreeBlobSerializer.SerializeFileEntryLine(entry), cancellationToken);
    }

    private async Task AppendChildrenAsync(string[] segments, CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < segments.Length - 1; depth++)
        {
            var parentPath = depth == 0 ? string.Empty : string.Join('/', segments, 0, depth);
            var childPath = string.Join('/', segments, 0, depth + 1);
            var childName = segments[depth] + "/";
            var childId = FileTreeStagingPaths.GetDirectoryId(childPath);
            var childrenPath = FileTreeStagingPaths.GetChildrenPath(_stagingRoot, FileTreeStagingPaths.GetDirectoryId(parentPath));

            await AppendLineAsync(childrenPath, $"{childId} D {childName}", cancellationToken);
        }
    }

    private async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        var nodeLock = _nodeLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await nodeLock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            nodeLock.Release();
        }
    }
}
