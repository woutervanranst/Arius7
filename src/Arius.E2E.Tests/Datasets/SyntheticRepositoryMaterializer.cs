using System.Security.Cryptography;
using System.Text;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryMaterializer
{
    public static async Task<SyntheticRepositoryState> MaterializeAsync(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion version, int seed, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);

        Directory.CreateDirectory(rootPath);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in definition.Files)
        {
            await WriteFileAsync(rootPath, file.Path, CreateBytes(seed, file.ContentId ?? file.Path, file.SizeBytes));
            files[file.Path] = await ComputeHashAsync(rootPath, file.Path);
        }

        if (version == SyntheticRepositoryVersion.V2)
            await ApplyV2MutationsAsync(definition, seed, rootPath, files);

        return new SyntheticRepositoryState(files);
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
        Dictionary<string, string> files)
    {
        foreach (var mutation in definition.V2Mutations)
        {
            switch (mutation.Kind)
            {
                case SyntheticMutationKind.Delete:
                    File.Delete(GetFullPath(rootPath, mutation.Path));
                    files.Remove(mutation.Path);
                    break;

                case SyntheticMutationKind.Rename:
                    var sourcePath = GetFullPath(rootPath, mutation.Path);
                    var targetPath = GetFullPath(rootPath, mutation.TargetPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(sourcePath, targetPath);

                    var existingHash = files[mutation.Path];
                    files.Remove(mutation.Path);
                    files[mutation.TargetPath!] = existingHash;
                    break;

                case SyntheticMutationKind.ChangeContent:
                case SyntheticMutationKind.Add:
                    var bytes = CreateBytes(seed, mutation.ReplacementContentId!, mutation.ReplacementSizeBytes!.Value);
                    await WriteFileAsync(rootPath, mutation.Path, bytes);
                    files[mutation.Path] = Convert.ToHexString(SHA256.HashData(bytes));
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

    static async Task<string> ComputeHashAsync(string rootPath, string relativePath)
    {
        var bytes = await File.ReadAllBytesAsync(GetFullPath(rootPath, relativePath));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
