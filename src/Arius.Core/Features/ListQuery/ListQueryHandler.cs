using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Streams repository entries from a snapshot with optional local filesystem merge.
/// A producer task walks the file tree depth-first and writes entries to a bounded channel,
/// so memory stays bounded regardless of repository size: in flight are at most one
/// directory's tree entries, one chunk-index lookup batch, and the channel buffer.
/// </summary>
public sealed class ListQueryHandler : IStreamQueryHandler<ListQuery, RepositoryEntry>
{
    private const int EntryChannelCapacity = 4096;
    private const int SizeLookupBatchSize  = 512;

    private readonly IChunkIndexService _index;
    private readonly IFileTreeService _fileTreeService;
    private readonly ISnapshotService _snapshotSvc;
    private readonly ILogger<ListQueryHandler> _logger;
    private readonly string _accountName;
    private readonly string _containerName;

    public ListQueryHandler(
        IChunkIndexService index,
        IFileTreeService fileTreeService,
        ISnapshotService snapshotSvc,
        ILogger<ListQueryHandler> logger,
        string accountName,
        string containerName)
    {
        _index = index;
        _fileTreeService = fileTreeService;
        _snapshotSvc = snapshotSvc;
        _logger = logger;
        _accountName = accountName;
        _containerName = containerName;
    }

    public async IAsyncEnumerable<RepositoryEntry> Handle(
        ListQuery command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = command.Options;

        _logger.LogInformation(
            "[ls] Start: account={Account} container={Container} version={Version} prefix={Prefix} filter={Filter} recursive={Recursive} localPath={LocalPath}",
            _accountName,
            _containerName,
            opts.Version ?? "latest",
            opts.Prefix is { } loggedPrefix ? loggedPrefix : "(none)",
            opts.Filter ?? "(none)",
            opts.Recursive,
            opts.LocalPath ?? "(none)");

        var snapshot = await _snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                opts.Version is null
                    ? "No snapshots found in this repository."
                    : $"Snapshot '{opts.Version}' not found.");
        }

        var prefix = opts.Prefix;
        var localRoot = ParseLocalRoot(opts.LocalPath);
        var localFileSystem = localRoot is { } root ? new RelativeFileSystem(root) : null;
        var (treeHash, relativeDirectory) = await ResolveStartingPointAsync(
            snapshot.RootHash,
            prefix,
            cancellationToken);

        var channel = Channel.CreateBounded<RepositoryEntry>(new BoundedChannelOptions(EntryChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(
            channel.Writer,
            treeHash,
            localFileSystem,
            relativeDirectory,
            opts.Filter,
            opts.Recursive,
            producerCts.Token);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                // ReadAllAsync only observes the token while waiting; check it per item so
                // cancellation is prompt even when the channel buffer still holds entries.
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
        finally
        {
            // Unblocks the producer if enumeration was abandoned before the channel drained.
            await producerCts.CancelAsync();
            await producer;
        }
    }

    /// <summary>
    /// Walks the tree and completes the channel; a failure (including cancellation) is surfaced
    /// to the consumer through the channel rather than thrown, so awaiting this task never throws.
    /// </summary>
    private async Task ProduceAsync(
        ChannelWriter<RepositoryEntry> writer,
        FileTreeHash? treeHash,
        RelativeFileSystem? localFileSystem,
        RelativePath relativeDirectory,
        string? filter,
        bool recursive,
        CancellationToken cancellationToken)
    {
        try
        {
            await WalkTreeAsync(writer, treeHash, localFileSystem, relativeDirectory, filter, recursive, cancellationToken).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception ex)
        {
            writer.Complete(ex);
        }
    }

    private async Task WalkTreeAsync(
        ChannelWriter<RepositoryEntry> writer,
        FileTreeHash? rootTreeHash,
        RelativeFileSystem? localFileSystem,
        RelativePath rootRelativeDirectory,
        string? filter,
        bool recursive,
        CancellationToken cancellationToken)
    {
        // Explicit depth-first stack instead of recursive iterators: yielding an entry costs O(1)
        // instead of O(depth), and pending state is just (path, hash) records per unvisited sibling.
        var pending = new Stack<RecursionTarget>();
        pending.Push(new RecursionTarget(
            RelativeDirectory: rootRelativeDirectory,
            TreeHash: rootTreeHash,
            HasLocalDirectory: localFileSystem is not null));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            IReadOnlyList<FileTreeEntry> cloudEntries = [];
            if (current.TreeHash is { } currentTreeHash)
            {
                cloudEntries = await _fileTreeService.ReadAsync(currentTreeHash, cancellationToken).ConfigureAwait(false);
            }

            var localSnapshot = BuildLocalDirectorySnapshot(
                current.HasLocalDirectory ? localFileSystem : null,
                current.RelativeDirectory);

            var recursionTargets = recursive ? new List<RecursionTarget>() : null;

            var yieldedDirectoryNames = new HashSet<PathSegment>();
            foreach (var entry in cloudEntries.OfType<DirectoryEntry>())
            {
                var directoryName = entry.Name;
                var relativePath = current.RelativeDirectory / directoryName;
                var existsLocally = localSnapshot.Directories.ContainsKey(directoryName);

                yieldedDirectoryNames.Add(directoryName);
                await writer.WriteAsync(
                    new RepositoryDirectoryEntry(relativePath, entry.FileTreeHash, ExistsInCloud: true, ExistsLocally: existsLocally),
                    cancellationToken).ConfigureAwait(false);

                recursionTargets?.Add(new RecursionTarget(
                    RelativeDirectory: relativePath,
                    TreeHash: entry.FileTreeHash,
                    HasLocalDirectory: existsLocally));
            }

            foreach (var localDirectory in localSnapshot.Directories.Values)
            {
                if (!yieldedDirectoryNames.Add(localDirectory.Name))
                {
                    continue;
                }

                await writer.WriteAsync(
                    new RepositoryDirectoryEntry(localDirectory.Path, TreeHash: null, ExistsInCloud: false, ExistsLocally: true),
                    cancellationToken).ConfigureAwait(false);

                recursionTargets?.Add(new RecursionTarget(
                    RelativeDirectory: localDirectory.Path,
                    TreeHash: null,
                    HasLocalDirectory: true));
            }

            // Cloud files are emitted in bounded batches: each batch resolves its sizes with one
            // chunk-index lookup, so a directory with millions of files never materializes in full.
            // A cloud file consumes ('Remove') its local counterpart even when the filter rejects it,
            // so the local-only pass below only sees files that have no cloud entry.
            var localFiles = localSnapshot.Files;
            var batch = new List<CloudFileCandidate>(SizeLookupBatchSize);
            foreach (var entry in cloudEntries.OfType<FileEntry>())
            {
                localFiles.Remove(entry.Name, out var localFile);

                if (!MatchesFilter(entry.Name, filter))
                {
                    continue;
                }

                batch.Add(new CloudFileCandidate(entry, localFile));
                if (batch.Count == SizeLookupBatchSize)
                {
                    await EmitCloudFileBatchAsync(writer, current.RelativeDirectory, batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await EmitCloudFileBatchAsync(writer, current.RelativeDirectory, batch, cancellationToken).ConfigureAwait(false);
            }

            foreach (var localFile in localFiles.Values)
            {
                if (!MatchesFilter(localFile.Name, filter))
                {
                    continue;
                }

                await writer.WriteAsync(
                    new RepositoryFileEntry(
                        RelativePath: localFile.Path,
                        ContentHash: null,
                        OriginalSize: localFile.FileSize,
                        Created: localFile.Created,
                        Modified: localFile.Modified,
                        ExistsInCloud: false,
                        ExistsLocally: true,
                        HasPointerFile: localFile.PointerExists,
                        BinaryExists: localFile.BinaryExists,
                        Hydrated: null),
                    cancellationToken).ConfigureAwait(false);
            }

            if (recursionTargets is { Count: > 0 })
            {
                // Reversed so the stack pops them in listing order (depth-first pre-order).
                for (var i = recursionTargets.Count - 1; i >= 0; i--)
                {
                    pending.Push(recursionTargets[i]);
                }
            }
        }
    }

    private async Task EmitCloudFileBatchAsync(
        ChannelWriter<RepositoryEntry> writer,
        RelativePath relativeDirectory,
        List<CloudFileCandidate> batch,
        CancellationToken cancellationToken)
    {
        var sizeLookup = await _index.LookupAsync(
            batch.Select(candidate => candidate.Entry.ContentHash).Distinct(),
            cancellationToken).ConfigureAwait(false);

        foreach (var candidate in batch)
        {
            var relativePath = relativeDirectory / candidate.Entry.Name;
            var localFile = candidate.LocalFile;
            long? originalSize = localFile?.FileSize;
            if (sizeLookup.TryGetValue(candidate.Entry.ContentHash, out var shardEntry))
                originalSize = shardEntry.OriginalSize;

            await writer.WriteAsync(
                new RepositoryFileEntry(
                    RelativePath: relativePath,
                    ContentHash: candidate.Entry.ContentHash,
                    OriginalSize: originalSize,
                    Created: candidate.Entry.Created,
                    Modified: candidate.Entry.Modified,
                    ExistsInCloud: true,
                    ExistsLocally: localFile is not null,
                    HasPointerFile: localFile?.PointerExists,
                    BinaryExists: localFile?.BinaryExists,
                    Hydrated: null),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(FileTreeHash? TreeHash, RelativePath RelativeDirectory)> ResolveStartingPointAsync(
        FileTreeHash rootHash,
        RelativePath? prefix,
        CancellationToken cancellationToken)
    {
        if (prefix is null)
        {
            return (rootHash, RelativePath.Root);
        }

        FileTreeHash? currentHash = rootHash;

        foreach (var segment in prefix.Value.Segments)
        {
            if (currentHash is null)
            {
                break;
            }

            var treeEntries = await _fileTreeService.ReadAsync(currentHash.Value, cancellationToken);

            var nextDirectory = treeEntries
                .OfType<DirectoryEntry>()
                .FirstOrDefault(e => PathSegmentEqualsIgnoreCase(e.Name, segment));

            currentHash = nextDirectory?.FileTreeHash;
        }

        return (currentHash, prefix.Value);
    }

    private LocalDirectorySnapshot BuildLocalDirectorySnapshot(RelativeFileSystem? localFileSystem, RelativePath currentRelativeDirectory)
    {
        if (localFileSystem is null || !localFileSystem.DirectoryExists(currentRelativeDirectory))
        {
            return LocalDirectorySnapshot.Empty;
        }

        var directories = new Dictionary<PathSegment, LocalDirectoryState>();
        try
        {
            foreach (var directory in localFileSystem.EnumerateDirectories(currentRelativeDirectory)
                         .OrderBy(d => d.Path.Name, PathSegmentOrdinalIgnoreCaseComparer.Instance))
            {
                directories[directory.Path.Name] = new LocalDirectoryState(directory.Path.Name, directory.Path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", currentRelativeDirectory);
        }

        IEnumerable<LocalFileEntry> fileInfos;
        try
        {
            // Immediate children only: nested files are handled when their own directory is walked,
            // and this keeps the snapshot bounded by directory width.
            fileInfos = localFileSystem.EnumerateFiles(currentRelativeDirectory, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.Path.Name, PathSegmentOrdinalIgnoreCaseComparer.Instance)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate files in: {Directory}", currentRelativeDirectory);
            fileInfos = [];
        }

        var files = LocalFileSnapshotBuilder.BuildFiles(
            fileInfos,
            localFileSystem.FileExists,
            localFileSystem.GetFileSize,
            localFileSystem.GetTimestamps,
            path => ReadPointerHash(localFileSystem, path));

        return new LocalDirectorySnapshot(directories, files);
    }

    private ContentHash? ReadPointerHash(RelativeFileSystem fileSystem, RelativePath relativePath)
    {
        try
        {
            var content = fileSystem.ReadAllText(relativePath).Trim();
            if (!ContentHash.TryParse(content, out var hash))
            {
                _logger.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", relativePath);
                return null;
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read pointer file: {RelPath}", relativePath);
            return null;
        }
    }

    private static LocalDirectory? ParseLocalRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : LocalDirectory.Parse(path);

    private static bool MatchesFilter(PathSegment fileName, string? filter) =>
        filter is null || fileName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool PathSegmentEqualsIgnoreCase(PathSegment left, PathSegment right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private sealed class PathSegmentOrdinalIgnoreCaseComparer : IComparer<PathSegment>
    {
        public static PathSegmentOrdinalIgnoreCaseComparer Instance { get; } = new();

        public int Compare(PathSegment x, PathSegment y) =>
            x.Compare(y, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CloudFileCandidate(FileEntry Entry, LocalFileState? LocalFile);

    private sealed record RecursionTarget(RelativePath RelativeDirectory, FileTreeHash? TreeHash, bool HasLocalDirectory);

    private sealed record LocalDirectoryState(PathSegment Name, RelativePath Path);

    private sealed record LocalDirectorySnapshot(
        IReadOnlyDictionary<PathSegment, LocalDirectoryState> Directories,
        Dictionary<PathSegment, LocalFileState> Files)
    {
        // Shared instance is safe: the walk only ever removes from Files, which is a no-op here.
        public static LocalDirectorySnapshot Empty { get; } =
            new LocalDirectorySnapshot(
                new Dictionary<PathSegment, LocalDirectoryState>(),
                new Dictionary<PathSegment, LocalFileState>());
    }
}
