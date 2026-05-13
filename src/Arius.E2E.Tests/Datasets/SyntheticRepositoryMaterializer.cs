using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.IO;

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
        var fileSystem = new RelativeFileSystem(LocalDirectory.Parse(rootPath));

        var files = new Dictionary<RelativePath, ContentHash>();

        foreach (var file in definition.Files)
        {
            var relativePath = RelativePath.Parse(file.Path);

            await fileSystem.WriteAllBytesAsync(relativePath, CreateBytes(seed, file.ContentId ?? file.Path, file.SizeBytes), CancellationToken.None);

            await using var stream = fileSystem.OpenRead(relativePath);
            files[relativePath] = await encryption.ComputeHashAsync(stream);
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
        var fileSystem = new RelativeFileSystem(LocalDirectory.Parse(targetRootPath));

        var files = new Dictionary<RelativePath, ContentHash>();
        foreach (var file in fileSystem.EnumerateFiles())
        {
            var relativePath = file.Path;

            await using var stream = fileSystem.OpenRead(relativePath);
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
        string rootPath,
        IEncryptionService encryption,
        Dictionary<RelativePath, ContentHash> files)
    {
        var rootDirectory = LocalDirectory.Parse(rootPath);
        var fileSystem = new RelativeFileSystem(rootDirectory);

        foreach (var mutation in definition.V2Mutations)
        {
            var mutationPath = RelativePath.Parse(mutation.Path);

            switch (mutation.Kind)
            {
                case SyntheticFileMutationKind.Delete:
                    fileSystem.DeleteFile(mutationPath);
                    files.Remove(mutationPath);
                    break;

                case SyntheticFileMutationKind.Rename:
                    var targetRelativePath = RelativePath.Parse(mutation.TargetPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(rootDirectory.Resolve(targetRelativePath))!);
                    File.Move(rootDirectory.Resolve(mutationPath), rootDirectory.Resolve(targetRelativePath));

                    var existingHash = files[mutationPath];
                    files.Remove(mutationPath);
                    files[targetRelativePath] = existingHash;
                    break;

                case SyntheticFileMutationKind.ChangeContent:
                case SyntheticFileMutationKind.Add:
                    var bytes = CreateBytes(seed, mutation.ReplacementContentId!, mutation.ReplacementSizeBytes!.Value);
                    await fileSystem.WriteAllBytesAsync(mutationPath, bytes, CancellationToken.None);
                    files[mutationPath] = encryption.ComputeHash(bytes);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }
    }
}
