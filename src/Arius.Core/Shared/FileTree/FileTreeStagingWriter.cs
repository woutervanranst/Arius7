using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace Arius.Core.Shared.FileTree;

[SharedWithinAssembly]
internal sealed class FileTreeStagingWriter : IDisposable
{
    private const int StripeCount = 256; // Note: we used to have a lock for every staging file, but that was unbounded. Now we have bounded memory by striping the locks

    private readonly Stripe[]           _lockStripes;
    private readonly RelativeFileSystem _stagingFileSystem;
    private          bool               _disposed;

    /// <summary>
    /// One gate and one reusable append handle per stripe; the gate protects both.
    /// </summary>
    private sealed class Stripe : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public RelativePath?          OpenPath;
        public Stream?                Handle;

        public void Dispose()
        {
            Handle?.Dispose();
            Gate.Dispose();
        }
    }

    // Emit each directory edge once; the reader treats duplicate edges as equivalent.
    private readonly ConcurrentDictionary<PathSegment, bool> _emittedDirectories = new();

    public FileTreeStagingWriter(LocalDirectory stagingRoot)
    {
        _stagingFileSystem = new RelativeFileSystem(stagingRoot);
        _lockStripes = Enumerable.Range(0, StripeCount)
            .Select(_ => new Stripe())
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

        var segments = filePath.Segments.ToArray();

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var parentPath = currentPath;
            currentPath = currentPath / segment;
            var directoryId = FileTreePaths.GetStagingDirectoryId(currentPath);

            // Claim each edge once; failed writes release the claim below.
            if (!_emittedDirectories.TryAdd(directoryId, true))
                continue;

            var nodePath = FileTreePaths.GetStagingNodePath(FileTreePaths.GetStagingDirectoryId(parentPath));

            try
            {
                await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedDirectoryEntryLine(directoryId.ToString(), segment), cancellationToken);
            }
            catch
            {
                // Allow a later writer to retry an edge whose append failed.
                _emittedDirectories.TryRemove(directoryId, out _);
                throw;
            }
        }
    }

    private async Task AppendLineAsync(RelativePath path, string line, CancellationToken cancellationToken)
    {
        var stripe = _lockStripes[(uint)path.GetHashCode() % (uint)_lockStripes.Length];
        await stripe.Gate.WaitAsync(cancellationToken);

        try
        {
            // Reuse the open handle for this node; close it when retargeting.
            if (stripe.OpenPath != path)
            {
                stripe.Handle?.Dispose();
                stripe.Handle   = _stagingFileSystem.OpenAppend(path);
                stripe.OpenPath = path;
            }

            // Keep the staging format platform-independent and consistent with FileTreeSerializer.
            var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length) + 1);
            try
            {
                var count = Encoding.UTF8.GetBytes(line, buffer);
                buffer[count++] = (byte)'\n';

                await stripe.Handle!.WriteAsync(buffer.AsMemory(0, count), cancellationToken);

                // Surface append failures before releasing the directory-edge claim.
                await stripe.Handle.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            stripe.Gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var stripe in _lockStripes)
            stripe.Dispose();
    }
}
