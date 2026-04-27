using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.IO;
using System.Security.Cryptography;
using System.Text;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryMaterializer
{
    public static async Task<SyntheticRepositoryState> MaterializeV1Async(
        SyntheticRepositoryDefinition definition,
        int seed,
        string rootPath,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(encryption);

        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);

        Directory.CreateDirectory(rootPath);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in definition.Files)
        {
            await WriteFileAsync(rootPath, file.Path, CreateBytes(seed, file.ContentId ?? file.Path, file.SizeBytes));

            await using var stream = File.OpenRead(GetFullPath(rootPath, file.Path));
            files[file.Path] = Convert.ToHexString(await encryption.ComputeHashAsync(stream));
        }

        return new SyntheticRepositoryState(rootPath, files);
    }

    public static async Task<SyntheticRepositoryState> MaterializeV2FromExistingAsync(
        SyntheticRepositoryDefinition definition,
        int seed,
        string sourceRootPath,
        string targetRootPath,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRootPath);
        ArgumentNullException.ThrowIfNull(encryption);

        if (Directory.Exists(targetRootPath))
            Directory.Delete(targetRootPath, recursive: true);

        FileSystemHelper.CopyDirectory(sourceRootPath, targetRootPath);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(targetRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(targetRootPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');

            await using var stream = File.OpenRead(filePath);
            files[relativePath] = Convert.ToHexString(await encryption.ComputeHashAsync(stream));
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
        string rootPath,
        IEncryptionService encryption,
        Dictionary<string, string> files)
    {
        foreach (var mutation in definition.V2Mutations)
        {
            switch (mutation.Kind)
            {
                case SyntheticFileMutationKind.Delete:
                    File.Delete(GetFullPath(rootPath, mutation.Path));
                    files.Remove(mutation.Path);
                    break;

                case SyntheticFileMutationKind.Rename:
                    var sourcePath = GetFullPath(rootPath, mutation.Path);
                    var targetPath = GetFullPath(rootPath, mutation.TargetPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(sourcePath, targetPath);

                    var existingHash = files[mutation.Path];
                    files.Remove(mutation.Path);
                    files[mutation.TargetPath!] = existingHash;
                    break;

                case SyntheticFileMutationKind.ChangeContent:
                case SyntheticFileMutationKind.Add:
                    var bytes = CreateBytes(seed, mutation.ReplacementContentId!, mutation.ReplacementSizeBytes!.Value);
                    await WriteFileAsync(rootPath, mutation.Path, bytes);
                    files[mutation.Path] = Convert.ToHexString(encryption.ComputeHash(bytes));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }
    }

    static string GetFullPath(string rootPath, string relativePath)
    {
        return Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    static async Task WriteFileAsync(string rootPath, string relativePath, byte[] bytes)
    {
        var fullPath = GetFullPath(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, bytes);
    }
}
