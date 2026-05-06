using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Features.ListQuery;

internal static class LocalFileSnapshotBuilder
{
    public static Dictionary<string, LocalFileState> BuildFiles(
        IEnumerable<LocalFileEntry> fileInfos,
        Func<RelativePath, bool> fileExists,
        Func<RelativePath, ContentHash?> readPointerHash)
    {
        var files = new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase);
        var enumeratedPaths = fileInfos.Select(file => file.Path).ToHashSet();

        foreach (var file in fileInfos)
        {
            if (TryGetPointerBinaryPath(file.Path, out var binaryPath))
            {
                var binaryName = binaryPath.Name.ToString();

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
                var hasPointer = enumeratedPaths.Contains(pointerPath) || fileExists(pointerPath);
                files[file.Path.Name.ToString()] = new LocalFileState(
                    Name: file.Path.Name.ToString(),
                    Path: file.Path,
                    BinaryExists: true,
                    PointerExists: hasPointer,
                    PointerHash: hasPointer ? readPointerHash(pointerPath) : null,
                    FileSize: file.Size,
                    Created: file.Created,
                    Modified: file.Modified);
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
    string Name,
    RelativePath Path,
    bool BinaryExists,
    bool PointerExists,
    ContentHash? PointerHash,
    long? FileSize,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);
