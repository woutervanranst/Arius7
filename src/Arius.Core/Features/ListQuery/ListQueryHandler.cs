using System.Diagnostics;
using System.Runtime.CompilerServices;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Streams repository entries for <c>ls</c> by walking two trees that mirror each other: the
/// snapshot's persisted filetree (remote) and the local directory tree (local).
///
/// Per directory, both sides are read and merged: the remote listing is the reference sequence
/// and the local listing is overlaid on top — every remote entry absorbs its local counterpart,
/// and the local leftovers trail as local-only. Files are emitted first, then subdirectories.
/// The walk is breadth-first (FIFO), so the shallow structure of a large repository streams out
/// before any subtree is descended. One chunk-index lookup per directory supplies file sizes and
/// storage tiers. Entries are yielded as soon as their directory is merged; memory is bounded by
/// directory width plus the traversal frontier, not by repository size.
///
/// The symmetric read/merge quartet:
/// <code>
/// | Step                 | Remote (repository filetree)        | Local (filesystem)                   |
/// |----------------------|-------------------------------------|--------------------------------------|
/// | Read                 | ReadRemoteDirectoryAsync(treeHash)  | ReadLocalDirectory(fileSystem, dir)  |
/// |                      |   → RemoteDirectoryListing          |   → LocalDirectoryListing            |
/// | Merge files          | MergeFilesAsync — remote files in tree order, each absorbing its local    |
/// |                      | counterpart; local leftovers trail as local-only                           |
/// | Merge subdirectories | MergeSubdirectories — remote subdirectories in tree order, flagged when   |
/// |                      | also present locally; local-only trail. Doubles as the walk worklist.     |
/// </code>
///
/// Overlay name matching is case-sensitive (exact tree names; case-variant files each get their
/// own row — presentation is the client's call), while <c>Prefix</c>/<c>Filter</c> are
/// case-insensitive user-typed conveniences.
/// </summary>
public sealed class ListQueryHandler(
    IChunkIndexService index,
    IFileTreeService fileTreeService,
    ISnapshotService snapshotSvc,
    ILogger<ListQueryHandler> logger,
    string accountName,
    string containerName) : IStreamQueryHandler<ListQuery, RepositoryEntry>
{
    public async IAsyncEnumerable<RepositoryEntry> Handle(
        ListQuery command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = command.Options;

        logger.LogInformation("[list] Start: account={Account} container={Container} version={Version} prefix={Prefix} filter={Filter} recursive={Recursive} localPath={LocalPath}", accountName, containerName, opts.Version ?? "latest", opts.Prefix is { } loggedPrefix ? loggedPrefix : "(none)", opts.Filter ?? "(none)", opts.Recursive, opts.LocalPath ?? "(none)");

        // Resolve the snapshot and descend to the prefix directory.
        logger.LogInformation("[phase] resolve-snapshot");
        var snapshot = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                opts.Version is null
                    ? "No snapshots found in this repository."
                    : $"Snapshot '{opts.Version}' not found.");
        }

        var localRoot       = ParseLocalRoot(opts.LocalPath);
        var localFileSystem = localRoot is { } root ? new RelativeFileSystem(root) : null;
        var (treeHash, startDirectory) = await ResolveStartingPointAsync(snapshot.RootHash, opts.Prefix, cancellationToken);

        // Walk, accumulating summary counters as entries stream past.
        logger.LogInformation("[phase] walk");
        var stopwatch           = Stopwatch.StartNew();
        var directoryCount      = 0;
        var bothCount           = 0; // in the repository and (pointer and/or binary) on disk
        var localOnlyCount      = 0;
        var repositoryOnlyCount = 0;
        var archivedCount       = 0;

        var start = new DirectoryToWalk(startDirectory, treeHash, ExistsLocally: localFileSystem is not null);
        await foreach (var entry in WalkAsync(start, localFileSystem, opts.Filter, opts.Recursive, cancellationToken))
        {
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

        logger.LogInformation("[list] Complete: {DirectoryCount} directories, {FileCount} files ({BothCount} local+repository, {LocalOnlyCount} local-only, {RepositoryOnlyCount} repository-only, {ArchivedCount} archived) in {Elapsed}", directoryCount, bothCount + localOnlyCount + repositoryOnlyCount, bothCount, localOnlyCount, repositoryOnlyCount, archivedCount, stopwatch.Elapsed);
    }

    // ── The walk ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Breadth-first walk over the merged remote/local tree: each directory's entries are emitted
    /// in full — files first, then subdirectories — before any descent.
    /// </summary>
    private async IAsyncEnumerable<RepositoryEntry> WalkAsync(
        DirectoryToWalk     start,
        RelativeFileSystem? localFileSystem,
        string?             filter,
        bool                recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Queue<DirectoryToWalk>();
        pending.Enqueue(start);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Dequeue();

            // Read both halves of the mirror.
            var remote = await ReadRemoteDirectoryAsync(directory.TreeHash, cancellationToken).ConfigureAwait(false);
            var local  = ReadLocalDirectory(localFileSystem, directory);

            // Files first: the remote listing is the reference sequence, local overlays it.
            await foreach (var file in MergeFilesAsync(directory.Path, remote, local, filter, cancellationToken).ConfigureAwait(false))
                yield return file;

            // Then subdirectories; the merged list doubles as the traversal worklist.
            foreach (var subdirectory in MergeSubdirectories(directory.Path, remote, local))
            {
                yield return new RepositoryDirectoryEntry(subdirectory.Path, DirectoryStateOf(subdirectory), subdirectory.TreeHash);

                if (recursive)
                    pending.Enqueue(subdirectory);
            }
        }
    }

    // ── Read: the two halves of the mirror ────────────────────────────────────

    /// <summary>Remote half of the read step: load the directory's persisted filetree node.</summary>
    private async Task<RemoteDirectoryListing> ReadRemoteDirectoryAsync(FileTreeHash? treeHash, CancellationToken cancellationToken)
    {
        if (treeHash is not { } hash)
            return RemoteDirectoryListing.Empty; // local-only directory: nothing remote to read

        var treeEntries = await fileTreeService.ReadAsync(hash, cancellationToken).ConfigureAwait(false);
        return RemoteDirectoryListing.From(treeEntries);
    }

    /// <summary>Local half of the read step: enumerate the directory's immediate children.</summary>
    private LocalDirectoryListing ReadLocalDirectory(RelativeFileSystem? localFileSystem, DirectoryToWalk directory)
    {
        if (localFileSystem is null || !directory.ExistsLocally || !localFileSystem.DirectoryExists(directory.Path))
            return LocalDirectoryListing.Empty;

        return LocalDirectoryReader.Read(localFileSystem, directory.Path, logger);
    }

    // ── Merge: remote is the reference sequence, local overlays it ────────────

    /// <summary>
    /// Merges the files of one directory. Each remote file absorbs (<c>Remove</c>s) its local
    /// counterpart — even when the filter rejects it — so the local-only pass at the end only sees
    /// files with no remote entry. One chunk-index lookup for the whole directory resolves sizes
    /// and storage tiers.
    /// </summary>
    private async IAsyncEnumerable<RepositoryFileEntry> MergeFilesAsync(
        RelativePath           directory,
        RemoteDirectoryListing remote,
        LocalDirectoryListing  local,
        string?                filter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Pair: every remote file absorbs its local counterpart.
        var pairs = new List<(FileEntry Remote, LocalFile? Local)>(remote.Files.Count);
        foreach (var remoteFile in remote.Files)
        {
            local.Files.Remove(remoteFile.Name, out var localFile);

            if (MatchesFilter(remoteFile.Name, filter))
                pairs.Add((remoteFile, localFile));
        }

        // Resolve: one lookup for the directory yields sizes and storage tiers.
        IReadOnlyDictionary<ContentHash, ShardEntry> indexEntries = new Dictionary<ContentHash, ShardEntry>();
        if (pairs.Count > 0)
        {
            indexEntries = await index.LookupAsync(
                pairs.Select(pair => pair.Remote.ContentHash).Distinct(),
                cancellationToken).ConfigureAwait(false);
        }

        // Emit: remote files in tree order…
        foreach (var (remoteFile, localFile) in pairs)
        {
            var state = RepositoryEntryState.Repository;
            long? size = localFile?.Size;
            if (indexEntries.TryGetValue(remoteFile.ContentHash, out var indexEntry))
            {
                size = indexEntry.OriginalSize;
                state |= indexEntry.StorageTierHint == BlobTier.Archive
                    ? RepositoryEntryState.RepositoryArchived
                    : RepositoryEntryState.RepositoryHydrated;
            }

            if (localFile is { } presentLocalFile)
                state |= LocalStateOf(presentLocalFile);

            yield return new RepositoryFileEntry(
                RelativePath: directory / remoteFile.Name,
                State: state,
                ContentHash: remoteFile.ContentHash,
                OriginalSize: size,
                Created: remoteFile.Created,
                Modified: remoteFile.Modified);
        }

        // …then the local leftovers as local-only.
        foreach (var localFile in local.Files.Values)
        {
            if (!MatchesFilter(localFile.Name, filter))
                continue;

            yield return new RepositoryFileEntry(
                RelativePath: directory / localFile.Name,
                State: LocalStateOf(localFile),
                ContentHash: null,
                OriginalSize: localFile.Size,
                Created: localFile.Created,
                Modified: localFile.Modified);
        }
    }

    /// <summary>
    /// Merges the subdirectories of one directory: remote subdirectories in tree order (flagged
    /// when they also exist locally), then local-only ones (sorted). The result doubles as the
    /// walk's traversal worklist.
    /// </summary>
    private static List<DirectoryToWalk> MergeSubdirectories(RelativePath parent, RemoteDirectoryListing remote, LocalDirectoryListing local)
    {
        var merged = new List<DirectoryToWalk>(remote.Subdirectories.Count);

        var remoteNames = new HashSet<PathSegment>();
        foreach (var remoteDirectory in remote.Subdirectories)
        {
            remoteNames.Add(remoteDirectory.Name);
            merged.Add(new DirectoryToWalk(
                Path: parent / remoteDirectory.Name,
                TreeHash: remoteDirectory.FileTreeHash,
                ExistsLocally: local.Subdirectories.Contains(remoteDirectory.Name)));
        }

        var localOnly = local.Subdirectories
            .Where(name => !remoteNames.Contains(name))
            .OrderBy(name => name, PathSegmentOrdinalIgnoreCaseComparer.Instance);
        foreach (var name in localOnly)
        {
            merged.Add(new DirectoryToWalk(parent / name, TreeHash: null, ExistsLocally: true));
        }

        return merged;
    }

    private static RepositoryEntryState DirectoryStateOf(DirectoryToWalk directory) =>
        (directory.TreeHash is not null ? RepositoryEntryState.Repository : RepositoryEntryState.None) |
        (directory.ExistsLocally ? RepositoryEntryState.LocalDirectory : RepositoryEntryState.None);

    private static RepositoryEntryState LocalStateOf(LocalFile localFile) =>
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LocalDirectory? ParseLocalRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : LocalDirectory.Parse(path);

    private static bool MatchesFilter(PathSegment fileName, string? filter) =>
        filter is null || fileName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool PathSegmentEqualsIgnoreCase(PathSegment left, PathSegment right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One directory on the walk worklist: where it is, its remote tree node (if any), and whether
    /// it exists locally — together these say which halves of the mirror there are to read.
    /// </summary>
    private sealed record DirectoryToWalk(RelativePath Path, FileTreeHash? TreeHash, bool ExistsLocally);
}
