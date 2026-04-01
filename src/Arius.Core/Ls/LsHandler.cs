using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Snapshot;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Arius.Core.Ls;

/// <summary>
/// Streams repository entries from a snapshot with optional local filesystem merge.
/// </summary>
public sealed class LsHandler : IStreamQueryHandler<LsCommand, RepositoryEntry>
{
    private const string PointerSuffix = ".pointer.arius";

    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService _encryption;
    private readonly ChunkIndexService _index;
    private readonly ILogger<LsHandler> _logger;
    private readonly string _accountName;
    private readonly string _containerName;

    public LsHandler(
        IBlobContainerService blobs,
        IEncryptionService encryption,
        ChunkIndexService index,
        ILogger<LsHandler> logger,
        string accountName,
        string containerName)
    {
        _blobs = blobs;
        _encryption = encryption;
        _index = index;
        _logger = logger;
        _accountName = accountName;
        _containerName = containerName;
    }

    public async IAsyncEnumerable<RepositoryEntry> Handle(
        LsCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = command.Options;

        _logger.LogInformation(
            "[ls] Start: account={Account} container={Container} version={Version} prefix={Prefix} filter={Filter} recursive={Recursive} localPath={LocalPath}",
            _accountName,
            _containerName,
            opts.Version ?? "latest",
            opts.Prefix ?? "(none)",
            opts.Filter ?? "(none)",
            opts.Recursive,
            opts.LocalPath ?? "(none)");

        var snapshotSvc = new SnapshotService(_blobs, _encryption);
        var snapshot = await snapshotSvc.ResolveAsync(opts.Version, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                opts.Version is null
                    ? "No snapshots found in this repository."
                    : $"Snapshot '{opts.Version}' not found.");
        }

        var prefix = NormalizePath(opts.Prefix);
        var localRoot = NormalizeLocalRoot(opts.LocalPath);
        var (treeHash, localDir, relativeDirectory) = await ResolveStartingPointAsync(
            snapshot.RootHash,
            localRoot,
            prefix,
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
        string? treeHash,
        string? localDir,
        string currentRelativeDirectory,
        string? filter,
        bool recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TreeBlob? treeBlob = null;
        if (treeHash is not null)
        {
            var blobName = BlobPaths.FileTree(treeHash);
            await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
            treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption, cancellationToken);
        }

        var localSnapshot = BuildLocalDirectorySnapshot(localDir, currentRelativeDirectory);
        var cloudEntries = treeBlob?.Entries ?? [];

        var yieldedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yieldedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursionTargets = new List<RecursionTarget>();

        foreach (var entry in cloudEntries.Where(e => e.Type == TreeEntryType.Dir))
        {
            var directoryName = NormalizeDirectoryEntryName(entry.Name);
            var relativePath = BuildDirectoryRelativePath(currentRelativeDirectory, directoryName);
            var existsLocally = localSnapshot.Directories.TryGetValue(directoryName, out var localDirectory);

            yieldedDirectoryNames.Add(directoryName);
            yield return new RepositoryDirectoryEntry(relativePath, entry.Hash, ExistsInCloud: true, ExistsLocally: existsLocally);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(
                    RelativeDirectory: CombineRelativePath(currentRelativeDirectory, directoryName),
                    TreeHash: entry.Hash,
                    LocalDirectory: existsLocally ? localDirectory!.FullPath : null));
            }
        }

        foreach (var localDirectory in localSnapshot.Directories.Values)
        {
            if (!yieldedDirectoryNames.Add(localDirectory.Name))
            {
                continue;
            }

            var relativePath = BuildDirectoryRelativePath(currentRelativeDirectory, localDirectory.Name);
            yield return new RepositoryDirectoryEntry(relativePath, TreeHash: null, ExistsInCloud: false, ExistsLocally: true);

            if (recursive)
            {
                recursionTargets.Add(new RecursionTarget(
                    RelativeDirectory: CombineRelativePath(currentRelativeDirectory, localDirectory.Name),
                    TreeHash: null,
                    LocalDirectory: localDirectory.FullPath));
            }
        }

        var visibleCloudFiles = cloudEntries
            .Where(e => e.Type == TreeEntryType.File)
            .Select(e => new CloudFileCandidate(e, localSnapshot.Files.GetValueOrDefault(e.Name)))
            .ToList();

        foreach (var candidate in visibleCloudFiles)
        {
            yieldedFileNames.Add(candidate.Entry.Name);
        }

        var hashes = visibleCloudFiles
            .Where(candidate => MatchesFilter(candidate.Entry.Name, filter))
            .Select(candidate => candidate.Entry.Hash)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var sizeLookup = hashes.Count == 0
            ? new Dictionary<string, ShardEntry>(StringComparer.Ordinal)
            : new Dictionary<string, ShardEntry>(await _index.LookupAsync(hashes, cancellationToken), StringComparer.Ordinal);

        foreach (var candidate in visibleCloudFiles)
        {
            if (!MatchesFilter(candidate.Entry.Name, filter))
            {
                continue;
            }

            var relativePath = CombineRelativePath(currentRelativeDirectory, candidate.Entry.Name);
            var localFile = candidate.LocalFile;
            long? originalSize = sizeLookup.TryGetValue(candidate.Entry.Hash, out var shardEntry)
                ? shardEntry.OriginalSize
                : localFile?.FileSize;

            yield return new RepositoryFileEntry(
                RelativePath: relativePath,
                ContentHash: candidate.Entry.Hash,
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

            var relativePath = CombineRelativePath(currentRelativeDirectory, localFile.Name);
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

    private async Task<(string? TreeHash, string? LocalDirectory, string RelativeDirectory)> ResolveStartingPointAsync(
        string rootHash,
        string? localRoot,
        string? prefix,
        CancellationToken cancellationToken)
    {
        if (prefix is null)
        {
            return (rootHash, localRoot, string.Empty);
        }

        var segments = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var currentHash = rootHash;

        foreach (var segment in segments)
        {
            if (currentHash is null)
            {
                break;
            }

            var blobName = BlobPaths.FileTree(currentHash);
            await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
            var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, _encryption, cancellationToken);

            var nextDirectory = treeBlob.Entries.FirstOrDefault(e =>
                e.Type == TreeEntryType.Dir
                && string.Equals(NormalizeDirectoryEntryName(e.Name), segment, StringComparison.OrdinalIgnoreCase));

            currentHash = nextDirectory?.Hash;
        }

        var localDirectory = localRoot;
        foreach (var segment in segments)
        {
            if (localDirectory is null)
            {
                break;
            }

            var candidate = Path.Combine(localDirectory, segment);
            localDirectory = Directory.Exists(candidate) ? candidate : null;
        }

        return (currentHash, localDirectory, prefix);
    }

    private LocalDirectorySnapshot BuildLocalDirectorySnapshot(string? localDir, string currentRelativeDirectory)
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
                    PointerHash: ReadPointerHash(file.FullName, CombineRelativePath(currentRelativeDirectory, file.Name)),
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
                    PointerHash: hasPointer ? ReadPointerHash(pointerPath, CombineRelativePath(currentRelativeDirectory, file.Name + PointerSuffix)) : null,
                    FileSize: file.Length,
                    Created: new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                    Modified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }

        return new LocalDirectorySnapshot(directories, files);
    }

    private string? ReadPointerHash(string fullPath, string relPath)
    {
        try
        {
            var content = File.ReadAllText(fullPath).Trim();
            if (content.Length == 0 || !content.All(IsHex))
            {
                _logger.LogWarning("Pointer file has invalid hex content, ignoring: {RelPath}", relPath);
                return null;
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read pointer file: {RelPath}", relPath);
            return null;
        }
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Replace('\\', '/').Trim('/');
    }

    private static string? NormalizeLocalRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static string NormalizeDirectoryEntryName(string name) => name.TrimEnd('/');

    private static string CombineRelativePath(string currentRelativeDirectory, string name) =>
        string.IsNullOrEmpty(currentRelativeDirectory) ? name : $"{currentRelativeDirectory}/{name}";

    private static string BuildDirectoryRelativePath(string currentRelativeDirectory, string directoryName) =>
        $"{CombineRelativePath(currentRelativeDirectory, directoryName)}/";

    private static bool MatchesFilter(string fileName, string? filter) =>
        filter is null || fileName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private sealed record CloudFileCandidate(TreeEntry Entry, LocalFileState? LocalFile);

    private sealed record RecursionTarget(string RelativeDirectory, string? TreeHash, string? LocalDirectory);

    private sealed record LocalDirectoryState(string Name, string FullPath);

    private sealed record LocalFileState(
        string Name,
        bool BinaryExists,
        bool PointerExists,
        string? PointerHash,
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
