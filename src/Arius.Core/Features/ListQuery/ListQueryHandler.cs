using System.Runtime.CompilerServices;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Streams repository entries from a snapshot with optional local filesystem merge.
/// </summary>
public sealed class ListQueryHandler : IStreamQueryHandler<ListQuery, RepositoryEntry>
{
    private readonly ChunkIndexService _index;
    private readonly FileTreeService _fileTreeService;
    private readonly SnapshotService _snapshotSvc;
    private readonly ILogger<ListQueryHandler> _logger;
    private readonly string _accountName;
    private readonly string _containerName;

    public ListQueryHandler(
        ChunkIndexService index,
        FileTreeService fileTreeService,
        SnapshotService snapshotSvc,
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

        await foreach (var entry in WalkDirectoryAsync(
                           treeHash,
                           localFileSystem,
                           relativeDirectory,
                           opts.Filter,
                           opts.Recursive,
                           cancellationToken))
        {
            yield return entry;
        }
    }

    private async IAsyncEnumerable<RepositoryEntry> WalkDirectoryAsync(
        FileTreeHash? treeHash,
        RelativeFileSystem? localFileSystem,
        RelativePath currentRelativeDirectory,
        string? filter,
        bool recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<FileTreeEntry>? treeEntries = null;
        if (treeHash is { } currentTreeHash)
        {
            treeEntries = await _fileTreeService.ReadAsync(currentTreeHash, cancellationToken);
        }

        var localSnapshot = BuildLocalDirectorySnapshot(localFileSystem, currentRelativeDirectory);
        var cloudEntries = treeEntries ?? [];

        var yieldedDirectoryNames = new HashSet<PathSegment>();
        var yieldedFileNames = new HashSet<PathSegment>();
        var recursionTargets = new List<RecursionTarget>();

        foreach (var entry in cloudEntries.OfType<DirectoryEntry>())
        {
            var directoryName = entry.Name;
            var relativePath = currentRelativeDirectory / directoryName;
            var existsLocally = localSnapshot.Directories.TryGetValue(directoryName, out _);

            yieldedDirectoryNames.Add(directoryName);
            yield return new RepositoryDirectoryEntry(relativePath, entry.FileTreeHash, ExistsInCloud: true, ExistsLocally: existsLocally);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(
                    RelativeDirectory: relativePath,
                    TreeHash: entry.FileTreeHash,
                    HasLocalDirectory: existsLocally));
            }
        }

        foreach (var localDirectory in localSnapshot.Directories.Values)
        {
            if (!yieldedDirectoryNames.Add(localDirectory.Name))
            {
                continue;
            }

            yield return new RepositoryDirectoryEntry(localDirectory.Path, TreeHash: null, ExistsInCloud: false, ExistsLocally: true);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(
                    RelativeDirectory: localDirectory.Path,
                    TreeHash: null,
                    HasLocalDirectory: true));
            }
        }

        var visibleCloudFiles = cloudEntries
            .OfType<FileEntry>()
            .Select(e => new CloudFileCandidate(e, localSnapshot.Files.GetValueOrDefault(e.Name)))
            .ToList();

        foreach (var candidate in visibleCloudFiles)
        {
            yieldedFileNames.Add(candidate.Entry.Name);
        }

        var hashes = visibleCloudFiles
            .Where(candidate => MatchesFilter(candidate.Entry.Name, filter))
            .Select(candidate => candidate.Entry.ContentHash)
            .Distinct()
            .ToList();
        var sizeLookup = hashes.Count == 0
            ? new Dictionary<ContentHash, ShardEntry>()
            : new Dictionary<ContentHash, ShardEntry>(await _index.LookupAsync(hashes, cancellationToken));

        foreach (var candidate in visibleCloudFiles)
        {
            if (!MatchesFilter(candidate.Entry.Name, filter))
            {
                continue;
            }

            var relativePath = currentRelativeDirectory / candidate.Entry.Name;
            var localFile = candidate.LocalFile;
            long? originalSize = localFile?.FileSize;
            if (sizeLookup.TryGetValue(candidate.Entry.ContentHash, out var shardEntry))
                originalSize = shardEntry.OriginalSize;

            yield return new RepositoryFileEntry(
                RelativePath: relativePath,
                ContentHash: candidate.Entry.ContentHash,
                OriginalSize: originalSize,
                Created: candidate.Entry.Created,
                Modified: candidate.Entry.Modified,
                ExistsInCloud: true,
                ExistsLocally: localFile is not null,
                HasPointerFile: localFile?.PointerExists,
                BinaryExists: localFile?.BinaryExists,
                Hydrated: null);
        }

        foreach (var localFile in localSnapshot.Files.Values)
        {
            if (yieldedFileNames.Contains(localFile.Name) || !MatchesFilter(localFile.Name, filter))
            {
                continue;
            }

            var relativePath = localFile.Path;
            yield return new RepositoryFileEntry(
                RelativePath: relativePath,
                ContentHash: null,
                OriginalSize: localFile.FileSize,
                Created: localFile.Created,
                Modified: localFile.Modified,
                ExistsInCloud: false,
                ExistsLocally: true,
                HasPointerFile: localFile.PointerExists,
                BinaryExists: localFile.BinaryExists,
                Hydrated: null);
        }

        if (!recursive)
        {
            yield break;
        }

        foreach (var target in recursionTargets)
        {
            await foreach (var child in WalkDirectoryAsync(
                               target.TreeHash,
                               target.HasLocalDirectory ? localFileSystem : null,
                               target.RelativeDirectory,
                               filter,
                               recursive,
                               cancellationToken))
            {
                yield return child;
            }
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
            fileInfos = localFileSystem.EnumerateFiles(currentRelativeDirectory, SearchOption.AllDirectories)
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
        IReadOnlyDictionary<PathSegment, LocalFileState> Files)
    {
        public static LocalDirectorySnapshot Empty { get; } =
            new LocalDirectorySnapshot(
                new Dictionary<PathSegment, LocalDirectoryState>(),
                new Dictionary<PathSegment, LocalFileState>());
    }
}
