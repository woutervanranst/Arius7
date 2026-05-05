using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Tests.Shared.IO;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryMaterializer
{
    public static async Task<SyntheticRepositoryState> MaterializeV1Async(
        SyntheticRepositoryDefinition definition,
        int seed,
        LocalRootPath rootPath,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encryption);

        if (rootPath.ExistsDirectory)
            rootPath.DeleteDirectory(recursive: true);

        rootPath.CreateDirectory();

        var files = new Dictionary<RelativePath, ContentHash>();

        foreach (var file in definition.Files)
        {
            var relativePath = RelativePath.Parse(file.Path);
            await WriteFileAsync(rootPath, relativePath, CreateBytes(seed, file.ContentId ?? file.Path, file.SizeBytes));

            await using var stream = File.OpenRead(relativePath.RootedAt(rootPath).FullPath);
            files[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        return new SyntheticRepositoryState(rootPath, files);
    }

    public static async Task<SyntheticRepositoryState> MaterializeV2FromExistingAsync(
        SyntheticRepositoryDefinition definition,
        int seed,
        LocalRootPath sourceRootPath,
        LocalRootPath targetRootPath,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encryption);

        await FileSystemHelper.CopyDirectoryAsync(sourceRootPath, targetRootPath);

        var files = new Dictionary<RelativePath, ContentHash>();
        foreach (var filePath in (targetRootPath / RelativePath.Root).EnumerateFiles(searchOption: SearchOption.AllDirectories))
        {
            var relativePath = filePath.RelativePath;

            await using var stream = filePath.OpenRead();
            files[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        await ApplyV2MutationsAsync(definition, seed, targetRootPath, encryption, files);

        return new SyntheticRepositoryState(targetRootPath, files);
    }

    static byte[] CreateBytes(int seed, string contentId, long sizeBytes)
    {
        var length = checked((int)sizeBytes);
        var bytes = new byte[length];
        var offset = 0;
        var block = 0;

        while (offset < bytes.Length)
        {
            var blockBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{contentId}:{block}"));
            var remaining = Math.Min(blockBytes.Length, bytes.Length - offset);
            Array.Copy(blockBytes, 0, bytes, offset, remaining);
            offset += remaining;
            block++;
        }

        return bytes;
    }

    static async Task ApplyV2MutationsAsync(
        SyntheticRepositoryDefinition definition,
        int seed,
        LocalRootPath rootPath,
        IEncryptionService encryption,
        Dictionary<RelativePath, ContentHash> files)
    {
        foreach (var mutation in definition.V2Mutations)
        {
            var relativePath = RelativePath.Parse(mutation.Path);

            switch (mutation.Kind)
            {
                case SyntheticFileMutationKind.Delete:
                    relativePath.RootedAt(rootPath).DeleteFile();
                    files.Remove(relativePath);
                    break;

                case SyntheticFileMutationKind.Rename:
                    var targetRelativePath = RelativePath.Parse(mutation.TargetPath!);
                    var sourcePath = relativePath.RootedAt(rootPath);
                    var targetPath = targetRelativePath.RootedAt(rootPath);
                    if (targetRelativePath.Parent is { } parent)
                        (rootPath / parent).CreateDirectory();

                    File.Move(sourcePath.FullPath, targetPath.FullPath);

                    var existingHash = files[relativePath];
                    files.Remove(relativePath);
                    files[targetRelativePath] = existingHash;
                    break;

                case SyntheticFileMutationKind.ChangeContent:
                case SyntheticFileMutationKind.Add:
                    var bytes = CreateBytes(seed, mutation.ReplacementContentId!, mutation.ReplacementSizeBytes!.Value);
                    await WriteFileAsync(rootPath, relativePath, bytes);
                    files[relativePath] = encryption.ComputeHash(bytes);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }
    }

    static async Task WriteFileAsync(LocalRootPath rootPath, RelativePath relativePath, byte[] bytes)
    {
        var fullPath = relativePath.RootedAt(rootPath);
        if (relativePath.Parent is { } parent)
            (rootPath / parent).CreateDirectory();

        await fullPath.WriteAllBytesAsync(bytes);
    }
}
