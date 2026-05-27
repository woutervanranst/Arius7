namespace Arius.Core.Features.ListQuery;

internal static class LocalFileSnapshotBuilder
{
    public static Dictionary<PathSegment, LocalFileState> BuildFiles(
        IEnumerable<LocalFileEntry> fileInfos,
        Func<RelativePath, bool> fileExists,
        Func<RelativePath, long> getFileSize,
        Func<RelativePath, (DateTimeOffset Created, DateTimeOffset Modified)> getTimestamps,
        Func<RelativePath, ContentHash?> readPointerHash)
    {
        var files = new Dictionary<PathSegment, LocalFileState>();
        var enumeratedPaths = fileInfos.Select(file => file.Path).ToHashSet();

        foreach (var file in fileInfos)
        {
            if (TryGetPointerBinaryPath(file.Path, out var binaryPath))
            {
                var binaryName = binaryPath.Name;

                if (files.TryGetValue(binaryName, out var existingBinary))
                {
                    files[binaryName] = existingBinary with
                    {
                        PointerExists = true,
                        PointerHash = readPointerHash(file.Path)
                    };
                    continue;
                }

                if (enumeratedPaths.Contains(binaryPath) || fileExists(binaryPath))
                {
                    continue;
                }

                files[binaryName] = new LocalFileState(
                    Name: binaryName,
                    Path: binaryPath,
                    BinaryExists: false,
                    PointerExists: true,
                    PointerHash: readPointerHash(file.Path),
                    FileSize: null,
                    Created: null,
                    Modified: null);
            }
            else
            {
                var pointerPath = file.Path.ToPointerPath();
                var hasPointer = enumeratedPaths.Contains(pointerPath);
                var (created, modified) = getTimestamps(file.Path);
                files[file.Path.Name] = new LocalFileState(
                    Name: file.Path.Name,
                    Path: file.Path,
                    BinaryExists: true,
                    PointerExists: hasPointer,
                    PointerHash: hasPointer ? readPointerHash(pointerPath) : null,
                    FileSize: getFileSize(file.Path),
                    Created: created,
                    Modified: modified);
            }
        }

        return files;
    }

    private static bool TryGetPointerBinaryPath(RelativePath path, out RelativePath binaryPath)
    {
        if (!path.IsPointerPath())
        {
            binaryPath = default;
            return false;
        }

        binaryPath = path.ToBinaryPath();
        return true;
    }
}

internal sealed record LocalFileState(
    PathSegment Name,
    RelativePath Path,
    bool BinaryExists,
    bool PointerExists,
    ContentHash? PointerHash,
    long? FileSize,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);
