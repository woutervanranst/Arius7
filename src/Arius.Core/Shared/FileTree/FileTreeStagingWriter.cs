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
    /// One lock plus one open append handle. Every write to a node goes through its stripe's gate, so the
    /// gate also guards that stripe's handle — which is what makes keeping it open safe without a second
    /// lock or a race between a write and an eviction. Handles are bounded by <see cref="StripeCount"/>,
    /// which matters on the small NAS hardware Arius targets.
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

        // Materialize once: RelativePath.Segments re-splits and re-parses the path on every
        // enumeration, so Take(Segments.Count() - 1) would parse the whole path twice on this hot
        // staging path. Iterate the segments by index instead, skipping the trailing file segment.
        var segments = filePath.Segments.ToArray();

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var parentPath = currentPath;
            currentPath = currentPath / segment;
            var directoryId = FileTreePaths.GetStagingDirectoryId(currentPath);

            // Claim the edge (but keep descending) so concurrent writers don't double-emit it.
            // TryAdd is atomic: exactly one writer wins the claim and writes the edge.
            if (!_emittedDirectories.TryAdd(directoryId, true))
                continue;

            var nodePath = FileTreePaths.GetStagingNodePath(FileTreePaths.GetStagingDirectoryId(parentPath));

            try
            {
                await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedDirectoryEntryLine(directoryId.ToString(), segment), cancellationToken);
            }
            catch
            {
                // The claim must reflect a committed edge: if the append fails (I/O error or
                // cancellation), release it so a later writer can re-emit. Otherwise the parent→child
                // edge is permanently skipped and its subtree orphaned.
                _emittedDirectories.TryRemove(directoryId, out _);
                throw;
            }
        }
    }

    private async Task AppendLineAsync(RelativePath path, string line, CancellationToken cancellationToken)
    {
        // path.GetHashCode(), not StringComparer.Ordinal.GetHashCode(path): RelativePath is a struct, so
        // the latter bound to IEqualityComparer.GetHashCode(object) — boxing on every append and then
        // falling through to path.GetHashCode() anyway, so the comparer was doing nothing.
        var stripe = _lockStripes[(uint)path.GetHashCode() % (uint)_lockStripes.Length];
        await stripe.Gate.WaitAsync(cancellationToken);

        try
        {
            // Reuse this stripe's handle when it is already pointed at the node. The archive walk is
            // depth-first, so consecutive files land in the same directory and hit the same node, making
            // this the common case. Re-targeting closes the previous handle first.
            if (stripe.OpenPath != path)
            {
                stripe.Handle?.Dispose();
                stripe.Handle   = _stagingFileSystem.OpenAppend(path);
                stripe.OpenPath = path;
            }

            // Fixed '\n' (not Environment.NewLine): staged lines are re-serialized by FileTreeSerializer
            // before hashing, but keep the staging format platform-independent and consistent with it.
            // Written as UTF-8 bytes with no BOM, matching what File.AppendAllTextAsync produced.
            var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length) + 1);
            try
            {
                var count = Encoding.UTF8.GetBytes(line, buffer);
                buffer[count++] = (byte)'\n';

                await stripe.Handle!.WriteAsync(buffer.AsMemory(0, count), cancellationToken);

                // Flush per line rather than buffering. Staging is disposable scratch, so buffering would
                // be durable enough, but flushing keeps the failure semantics exact: a write error surfaces
                // from this call, which is what the caller's claim-release in AppendDirectoryEntriesAsync
                // depends on. The win here is dropping the per-line open/create-directory/close, not the
                // write itself.
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
