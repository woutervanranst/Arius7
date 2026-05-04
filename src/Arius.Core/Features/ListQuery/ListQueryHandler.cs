using System.Runtime.CompilerServices;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.ListQuery;

/// <summary>
/// Streams repository entries from a snapshot with optional local filesystem merge.
/// </summary>
public sealed class ListQueryHandler : IStreamQueryHandler<ListQuery, RepositoryEntry>
{
    private const string PointerSuffix = ".pointer.arius";

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
            opts.Prefix?.ToString() ?? "(none)",
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

        var localRoot = NormalizeLocalRoot(opts.LocalPath);
        var (treeHash, localDir, relativeDirectory) = await ResolveStartingPointAsync(
            snapshot.RootHash,
            localRoot,
            opts.Prefix,
            cancellationToken);

        await foreach (var entry in WalkDirectoryAsync(
                           treeHash,
                           localDir,
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
        string? localDir,
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

        var localSnapshot = BuildLocalDirectorySnapshot(localDir, currentRelativeDirectory);
        var cloudEntries = treeEntries ?? [];

        var yieldedDirectoryNames = new HashSet<PathSegment>(PathSegment.OrdinalIgnoreCaseComparer);
        var yieldedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursionTargets = new List<RecursionTarget>();

        foreach (var entry in cloudEntries.OfType<DirectoryEntry>())
        {
            var directorySegment = entry.GetDirectoryName();
            var directoryName    = directorySegment.ToString();
            var existsLocally    = localSnapshot.Directories.TryGetValue(directoryName, out var localDirectory);

            yieldedDirectoryNames.Add(directorySegment);
            var relativePath = currentRelativeDirectory / directorySegment;
            yield return new RepositoryDirectoryEntry(relativePath, entry.FileTreeHash, ExistsInCloud: true, ExistsLocally: existsLocally);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(RelativeDirectory: relativePath, TreeHash: entry.FileTreeHash, LocalDirectory: existsLocally ? localDirectory!.FullPath : null));
            }
        }

        foreach (var localDirectory in localSnapshot.Directories.Values)
        {
            var localDirectorySegment = PathSegment.Parse(localDirectory.Name);

            if (!yieldedDirectoryNames.Add(localDirectorySegment))
            {
                continue;
            }

            var relativePath = currentRelativeDirectory / localDirectorySegment;
            yield return new RepositoryDirectoryEntry(relativePath, TreeHash: null, ExistsInCloud: false, ExistsLocally: true);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(
                    RelativeDirectory: relativePath,
                    TreeHash: null,
                    LocalDirectory: localDirectory.FullPath));
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

            var relativePath = currentRelativeDirectory / PathSegment.Parse(candidate.Entry.Name);
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

            var relativePath = currentRelativeDirectory / PathSegment.Parse(localFile.Name);
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
                               target.LocalDirectory,
                               target.RelativeDirectory,
                               filter,
                               recursive,
                               cancellationToken))
            {
                yield return child;
            }
        }
    }

    private async Task<(FileTreeHash? TreeHash, string? LocalDirectory, RelativePath RelativeDirectory)> ResolveStartingPointAsync(
        FileTreeHash rootHash,
        string? localRoot,
        RelativePath? prefix,
        CancellationToken cancellationToken)
    {
        if (prefix is null)
        {
            return (rootHash, localRoot, RelativePath.Root);
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
                .FirstOrDefault(e => e.GetDirectoryName().Equals(segment, StringComparison.OrdinalIgnoreCase));

            currentHash = nextDirectory?.FileTreeHash;
        }

        var localDirectory = localRoot;
        foreach (var segment in prefix.Value.Segments)
        {
            if (localDirectory is null)
            {
                break;
            }

            var candidate = Path.Combine(localDirectory, segment.ToString());
            localDirectory = Directory.Exists(candidate) ? candidate : null;
        }

        return (currentHash, localDirectory, prefix.Value);
    }

    private LocalDirectorySnapshot BuildLocalDirectorySnapshot(string? localDir, RelativePath currentRelativeDirectory)
    {
        if (localDir is null || !Directory.Exists(localDir))
        {
            return LocalDirectorySnapshot.Empty;
        }

        var directories = new Dictionary<string, LocalDirectoryState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in new DirectoryInfo(localDir).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                directories[directory.Name] = new LocalDirectoryState(directory.Name, directory.FullName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", localDir);
        }

        var files = new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> fileInfos;
        try
        {
            fileInfos = new DirectoryInfo(localDir).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate files in: {Directory}", localDir);
            fileInfos = [];
        }

        foreach (var file in fileInfos)
        {
            if (file.Name.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var binaryName = file.Name[..^PointerSuffix.Length];
                var binaryPath = Path.Combine(localDir, binaryName);
                if (File.Exists(binaryPath))
                {
                    continue;
                }

                files[binaryName] = new LocalFileState(
                    Name: binaryName,
                    BinaryExists: false,
                    PointerExists: true,
                    PointerHash: ReadPointerHash(file.FullName, (currentRelativeDirectory / PathSegment.Parse(file.Name)).ToString()),
                    FileSize: null,
                    Created: null,
                    Modified: null);
            }
            else
            {
                var pointerPath = file.FullName + PointerSuffix;
                var hasPointer = File.Exists(pointerPath);
                files[file.Name] = new LocalFileState(
                    Name: file.Name,
                    BinaryExists: true,
                    PointerExists: hasPointer,
                    PointerHash: hasPointer ? ReadPointerHash(pointerPath, (currentRelativeDirectory / PathSegment.Parse(file.Name + PointerSuffix)).ToString()) : null,
                    FileSize: file.Length,
                    Created: new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                    Modified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }

        return new LocalDirectorySnapshot(directories, files);
    }

    private ContentHash? ReadPointerHash(string fullPath, string relPath)
    {
        try
        {
            var content = File.ReadAllText(fullPath).Trim();
            if (!ContentHash.TryParse(content, out var hash))
            {
                _logger.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", relPath);
                return null;
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read pointer file: {RelPath}", relPath);
            return null;
        }
    }

    private static string? NormalizeLocalRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static bool MatchesFilter(string fileName, string? filter) =>
        filter is null || fileName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private sealed record CloudFileCandidate(FileEntry Entry, LocalFileState? LocalFile);

    private sealed record RecursionTarget(RelativePath RelativeDirectory, FileTreeHash? TreeHash, string? LocalDirectory);

    private sealed record LocalDirectoryState(string Name, string FullPath);

    private sealed record LocalFileState(
        string Name,
        bool BinaryExists,
        bool PointerExists,
        ContentHash? PointerHash,
        long? FileSize,
        DateTimeOffset? Created,
        DateTimeOffset? Modified);

    private sealed record LocalDirectorySnapshot(
        IReadOnlyDictionary<string, LocalDirectoryState> Directories,
        IReadOnlyDictionary<string, LocalFileState> Files)
    {
        public static LocalDirectorySnapshot Empty { get; } =
            new LocalDirectorySnapshot(
                new Dictionary<string, LocalDirectoryState>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase));
    }
}
