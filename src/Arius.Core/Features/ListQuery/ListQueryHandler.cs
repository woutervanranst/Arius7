using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Streams repository entries from a snapshot, overlaying the local filesystem on top.
///
/// The pipeline is a set of stages connected by bounded <see cref="System.Threading.Channels.Channel{T}"/>s.
/// Each stage drains its input channel, does its work, and writes to its output channel; a stage
/// completes its output writer when it finishes (faults are propagated through
/// <c>Writer.Complete(exception)</c> so the downstream consumer rethrows them). Memory stays bounded
/// regardless of repository size: in flight are at most one directory's tree entries, one
/// chunk-index lookup batch, and the channel buffers.
///
/// ## Stages
///
/// 1. **Walk** (×1) — iterative depth-first walk of the snapshot file tree (explicit stack; O(1)
///    per entry instead of O(depth) nested iterators). Per directory, the file-tree node is the
///    reference sequence and the local directory (enumerated once, immediate children only) is
///    *overlaid* on top: each repository file consumes its local counterpart, and the leftovers
///    are emitted last as local-only — repository entries first, local-only last, no
///    union/distinct pass over both sets. Overlay name matching is case-sensitive (exact tree
///    names; case-variant files each get their own row — presentation is the client's call),
///    while <c>Prefix</c>/<c>Filter</c> are case-insensitive user-typed conveniences.
///    Directory entries and local-only files are emitted
///    fully resolved; repository files are emitted as candidates that still need size + tier.
/// 2. **Resolve** (×1) — buffers consecutive candidates (≤ <see cref="ResolveBatchSize"/>) and
///    resolves each batch with one <see cref="IChunkIndexService.LookupAsync(IEnumerable{ContentHash}, CancellationToken)"/>
///    call (sizes + storage-tier hints → <see cref="RepositoryEntryState"/> flags). Any pending
///    batch is flushed before a resolved entry is forwarded, so the listing order is preserved;
///    batches thus flush naturally at directory boundaries.
/// 3. **Consume** — <c>Handle</c> yields from the entry channel; abandoning enumeration cancels
///    the stages via a linked CTS.
///
/// ```
/// Walk (1) ─► walkItemChannel ─► Resolve (2) ─► entryChannel ─► Handle (3) (yield)
/// ```
///
/// ## Channels
///
/// | Channel           | Writer      | Reader      | Capacity    | Notes                                                  |
/// |-------------------|-------------|-------------|-------------|--------------------------------------------------------|
/// | `walkItemChannel` | Walk (1)    | Resolve (2) | bounded (N) | Backpressure caps how far the walk runs ahead.         |
/// | `entryChannel`    | Resolve (2) | Handle (3)  | bounded (N) | Backpressure caps how far resolution runs ahead of the consumer. |
/// </summary>
public sealed class ListQueryHandler(
    IChunkIndexService index,
    IFileTreeService fileTreeService,
    ISnapshotService snapshotSvc,
    ILogger<ListQueryHandler> logger,
    string accountName,
    string containerName) : IStreamQueryHandler<ListQuery, RepositoryEntry>
{
    // ── Tuning knobs ──────────────────────────────────────────────────────────

    private const int ChannelCapacity  = 4096;
    private const int ResolveBatchSize = 32; // small: first results must not wait on a large batch

    // ── Dependencies ──────────────────────────────────────────────────────────

    public async IAsyncEnumerable<RepositoryEntry> Handle(
        ListQuery command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = command.Options;

        // ── Operation start marker ────────────────────────────────────────────
        logger.LogInformation("[list] Start: account={Account} container={Container} version={Version} prefix={Prefix} filter={Filter} recursive={Recursive} localPath={LocalPath}", accountName, containerName, opts.Version ?? "latest", opts.Prefix is { } loggedPrefix ? loggedPrefix : "(none)", opts.Filter ?? "(none)", opts.Recursive, opts.LocalPath ?? "(none)");

        // ── Resolve snapshot and starting point ───────────────────────────────
        logger.LogInformation("[phase] resolve-snapshot");
        var snapshot = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException(opts.Version is null ? "No snapshots found in this repository." : $"Snapshot '{opts.Version}' not found.");
        }

        var localFileSystem = ParseLocalFileSystem(opts.LocalPath);
        var (treeHash, relativeDirectory) = await ResolveStartingPointAsync(snapshot.RootHash, opts.Prefix, cancellationToken);

        // ── Channels between stages ───────────────────────────────────────────
        var fileSystemItemChannel = Channel.CreateBounded<FileSystemItem>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter        = true, SingleReader = true });
        var entryChannel    = Channel.CreateBounded<RepositoryEntry>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter = true, SingleReader = true });

        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ── Stage 1: Walk Repository FileTree & Local File System ×1 ──────────────────────────────────────────────────
        logger.LogInformation("[phase] walk");
        var walkFileSystemTask = Task.Run(async () =>
        {
            try
            {
                await WalkFileTreeAsync(fileSystemItemChannel.Writer, treeHash, localFileSystem, relativeDirectory, opts.Filter, opts.Recursive, stageCts.Token);
                fileSystemItemChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                fileSystemItemChannel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        await walkFileSystemTask;
        var x = await fileSystemItemChannel.Reader.ReadAllAsync().ToArrayAsync();


        yield return null;

        // ── Stage 2: Resolve ×1 ───────────────────────────────────────────────
        logger.LogInformation("[phase] resolve");
        var resolveTask = Task.Run(async () =>
        {
            try
            {
                await ResolveAsync(fileSystemItemChannel.Reader, entryChannel.Writer, stageCts.Token);
                entryChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                entryChannel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        // ── Stage 3: Consume ──────────────────────────────────────────────────
        var directoryCount  = 0;
        var bothCount       = 0; // in the repository and (pointer and/or binary) on disk
        var localOnlyCount  = 0;
        var repositoryOnlyCount = 0;
        var archivedCount   = 0;
        try
        {
            await foreach (var entry in entryChannel.Reader.ReadAllAsync(cancellationToken))
            {
                // ReadAllAsync only observes the token while waiting; check it per item so
                // cancellation is prompt even when the channel buffer still holds entries.
                cancellationToken.ThrowIfCancellationRequested();

                if (entry is RepositoryDirectoryEntry)
                {
                    directoryCount++;
                }
                else
                {
                    var inRepository = entry.State.HasFlag(RepositoryEntryState.Repository);
                    var onDisk       = (entry.State & (RepositoryEntryState.LocalPointer | RepositoryEntryState.LocalBinary)) != 0;
                    if (inRepository && onDisk) 
                        bothCount++;
                    else if (inRepository) 
                        repositoryOnlyCount++;
                    else 
                        localOnlyCount++;

                    if (entry.State.HasFlag(RepositoryEntryState.RepositoryArchived))
                        archivedCount++;
                }

                yield return entry;
            }

            logger.LogInformation("[list] Complete: {DirectoryCount} directories, {FileCount} files ({BothCount} local+repository, {LocalOnlyCount} local-only, {RepositoryOnlyCount} repository-only, {ArchivedCount} archived)", directoryCount, bothCount + localOnlyCount + repositoryOnlyCount, bothCount, localOnlyCount, repositoryOnlyCount, archivedCount);
        }
        finally
        {
            // Unblocks the stages if enumeration was abandoned before the channels drained.
            await stageCts.CancelAsync();
            await Task.WhenAll(walkFileSystemTask, resolveTask);
        }
    }

    // ── Stage 1: Walk ─────────────────────────────────────────────────────────

    private async Task WalkFileTreeAsync(
        ChannelWriter<FileSystemItem> writer,
        FileTreeHash?           rootTreeHash,
        RelativeFileSystem?     localFileSystem,
        RelativePath            rootRelativeDirectory,
        string?                 filter,
        bool                    recursive,
        CancellationToken       cancellationToken)
    {
        var pending = new Stack<DirectoryToWalk>();
        pending.Push(new DirectoryToWalk(RelativeDirectory: rootRelativeDirectory, TreeHash: rootTreeHash, HasLocalDirectory: localFileSystem is not null));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            IReadOnlyList<FileTreeEntry> remoteFileTreeEntries = [];
            if (current.TreeHash is { } currentTreeHash)
            {
                remoteFileTreeEntries = await fileTreeService.ReadAsync(currentTreeHash, cancellationToken).ConfigureAwait(false);
            }

            var localSnapshot = BuildLocalDirectorySnapshot(current.HasLocalDirectory ? localFileSystem : null, current.RelativeDirectory);

            var childDirectories = recursive ? new List<DirectoryToWalk>() : null;

            // Directories: repository first, then local-only.
            var emittedDirectoryNames = new HashSet<PathSegment>();
            foreach (var repositoryDirectory in remoteFileTreeEntries.OfType<DirectoryEntry>())
            {
                var directoryName = repositoryDirectory.Name;
                var relativePath  = current.RelativeDirectory / directoryName;
                var existsLocally = localSnapshot.Directories.ContainsKey(directoryName);
                var state         = RepositoryEntryState.Repository | (existsLocally ? RepositoryEntryState.LocalDirectory : RepositoryEntryState.None);

                emittedDirectoryNames.Add(directoryName);
                await writer.WriteAsync(
                    FileSystemItem.Resolved(new RepositoryDirectoryEntry(relativePath, state, repositoryDirectory.FileTreeHash)),
                    cancellationToken).ConfigureAwait(false);

                childDirectories?.Add(new DirectoryToWalk(
                    RelativeDirectory: relativePath,
                    TreeHash: repositoryDirectory.FileTreeHash,
                    HasLocalDirectory: existsLocally));
            }

            foreach (var localDirectory in localSnapshot.Directories.Values)
            {
                if (!emittedDirectoryNames.Add(localDirectory.Name))
                {
                    continue;
                }

                await writer.WriteAsync(FileSystemItem.Resolved(new RepositoryDirectoryEntry(localDirectory.Path, RepositoryEntryState.LocalDirectory, TreeHash: null)), cancellationToken).ConfigureAwait(false);

                childDirectories?.Add(new DirectoryToWalk(RelativeDirectory: localDirectory.Path, TreeHash: null, HasLocalDirectory: true));
            }

            // Files: the repository (file tree) is the reference sequence; each repository file
            // consumes ('Remove') its local counterpart — even when the filter rejects it — so the
            // local-only pass below only sees files that have no repository entry.
            var localFiles = localSnapshot.Files;
            foreach (var repositoryFile in remoteFileTreeEntries.OfType<FileEntry>())
            {
                localFiles.Remove(repositoryFile.Name, out var localFile);

                if (!MatchesFilter(repositoryFile.Name, filter))
                {
                    continue;
                }

                await writer.WriteAsync(FileSystemItem.Unresolved(new RepositoryFileCandidate(repositoryFile, localFile, current.RelativeDirectory)), cancellationToken).ConfigureAwait(false);
            }

            foreach (var localFile in localFiles.Values)
            {
                if (!MatchesFilter(localFile.Name, filter))
                {
                    continue;
                }

                await writer.WriteAsync(FileSystemItem.Resolved(new RepositoryFileEntry(RelativePath: localFile.Path, State: LocalStateOf(localFile), ContentHash: null, OriginalSize: localFile.FileSize, Created: localFile.Created, Modified: localFile.Modified)), cancellationToken).ConfigureAwait(false);
            }

            if (childDirectories is { Count: > 0 })
            {
                // Reversed so the stack pops them in listing order (depth-first pre-order).
                for (var i = childDirectories.Count - 1; i >= 0; i--)
                {
                    pending.Push(childDirectories[i]);
                }
            }
        }
    }

    // ── Stage 2: Resolve ──────────────────────────────────────────────────────

    private async Task ResolveAsync(
        ChannelReader<FileSystemItem>        reader,
        ChannelWriter<RepositoryEntry> writer,
        CancellationToken              cancellationToken)
    {
        var batch = new List<RepositoryFileCandidate>(ResolveBatchSize);

        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Candidate is { } candidate)
            {
                batch.Add(candidate);
                if (batch.Count == ResolveBatchSize)
                {
                    await ResolveBatchAsync(writer, batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }

                continue;
            }

            // A resolved entry must not overtake the candidates emitted before it.
            if (batch.Count > 0)
            {
                await ResolveBatchAsync(writer, batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            await writer.WriteAsync(item.Entry!, cancellationToken).ConfigureAwait(false);
        }

        if (batch.Count > 0)
        {
            await ResolveBatchAsync(writer, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResolveBatchAsync(
        ChannelWriter<RepositoryEntry> writer,
        List<RepositoryFileCandidate>  batch,
        CancellationToken              cancellationToken)
    {
        var indexEntries = await index.LookupAsync(
            batch.Select(candidate => candidate.RepositoryFile.ContentHash).Distinct(),
            cancellationToken).ConfigureAwait(false);

        foreach (var candidate in batch)
        {
            var repositoryFile = candidate.RepositoryFile;
            var localFile      = candidate.LocalFile;

            var state = RepositoryEntryState.Repository;
            long? originalSize = localFile?.FileSize;
            if (indexEntries.TryGetValue(repositoryFile.ContentHash, out var indexEntry))
            {
                originalSize = indexEntry.OriginalSize;
                state |= indexEntry.StorageTierHint == BlobTier.Archive
                    ? RepositoryEntryState.RepositoryArchived
                    : RepositoryEntryState.RepositoryHydrated;
            }

            if (localFile is { } presentLocalFile)
                state |= LocalStateOf(presentLocalFile);

            await writer.WriteAsync(
                new RepositoryFileEntry(
                    RelativePath: candidate.RelativeDirectory / repositoryFile.Name,
                    State: state,
                    ContentHash: repositoryFile.ContentHash,
                    OriginalSize: originalSize,
                    Created: repositoryFile.Created,
                    Modified: repositoryFile.Modified),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static RepositoryEntryState LocalStateOf(LocalFileState localFile) =>
        (localFile.PointerExists ? RepositoryEntryState.LocalPointer : RepositoryEntryState.None) |
        (localFile.BinaryExists ? RepositoryEntryState.LocalBinary : RepositoryEntryState.None);

    // ── Starting point ────────────────────────────────────────────────────────

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

            var treeEntries = await fileTreeService.ReadAsync(currentHash.Value, cancellationToken);

            var nextDirectory = treeEntries
                .OfType<DirectoryEntry>()
                .FirstOrDefault(e => PathSegmentEqualsIgnoreCase(e.Name, segment));

            currentHash = nextDirectory?.FileTreeHash;
        }

        return (currentHash, prefix.Value);
    }

    // ── Local overlay ─────────────────────────────────────────────────────────

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
            logger.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", currentRelativeDirectory);
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
            logger.LogWarning(ex, "Could not enumerate files in: {Directory}", currentRelativeDirectory);
            fileInfos = [];
        }

        // The listing only needs pointer/binary *existence*, never the pointer's hash —
        // so pointer file contents are not read (one avoided file read per pointer).
        var files = LocalFileSnapshotBuilder.BuildFiles(
            fileInfos,
            localFileSystem.FileExists,
            localFileSystem.GetFileSize,
            localFileSystem.GetTimestamps,
            readPointerHash: _ => null);

        return new LocalDirectorySnapshot(directories, files);
    }

    private static RelativeFileSystem? ParseLocalFileSystem(string? path)
    {
        LocalDirectory? localRoot = string.IsNullOrWhiteSpace(path) ? null : LocalDirectory.Parse(path);
        return localRoot is { } root ? new RelativeFileSystem(root) : null;
    }

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

    // ── Pipeline records ──────────────────────────────────────────────────────

    /// <summary>
    /// One item flowing from Walk to Resolve: either an already-resolved entry (directories,
    /// local-only files) or a repository-file candidate that still needs its size + tier.
    /// </summary>
    private sealed record FileSystemItem(RepositoryEntry? Entry, RepositoryFileCandidate? Candidate)
    {
        public static FileSystemItem Resolved(RepositoryEntry entry) => new(entry, null);

        public static FileSystemItem Unresolved(RepositoryFileCandidate candidate) => new(null, candidate);
    }

    private sealed record RepositoryFileCandidate(FileEntry RepositoryFile, LocalFileState? LocalFile, RelativePath RelativeDirectory);

    private sealed record DirectoryToWalk(RelativePath RelativeDirectory, FileTreeHash? TreeHash, bool HasLocalDirectory);


    private sealed record LocalDirectoryState(PathSegment Name, RelativePath Path);

    private sealed record LocalDirectorySnapshot(IReadOnlyDictionary<PathSegment, LocalDirectoryState> Directories, Dictionary<PathSegment, LocalFileState> Files)
    {
        // Shared instance is safe: the walk only ever removes from Files, which is a no-op here.
        public static LocalDirectorySnapshot Empty { get; } =  new(new Dictionary<PathSegment, LocalDirectoryState>(), []);
    }
}
