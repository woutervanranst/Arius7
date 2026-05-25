using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.IO;
using System.Security.Cryptography;
using System.Text;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryMaterializer
{
    public static async Task<SyntheticRepositoryState> MaterializeV1Async(
        SyntheticRepositoryDefinition definition,
        int seed,
        LocalDirectory rootDirectory,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encryption);

        var fileSystem = new RelativeFileSystem(rootDirectory);

        fileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        fileSystem.CreateDirectory(RelativePath.Root);

        var files = new Dictionary<RelativePath, ContentHash>();

        foreach (var file in definition.Files)
        {
            await fileSystem.WriteAllBytesAsync(file.Path, CreateBytes(seed, file.ContentId ?? file.Path.ToString(), file.SizeBytes), CancellationToken.None);

            await using var stream = fileSystem.OpenRead(file.Path);
            files[file.Path] = await encryption.ComputeHashAsync(stream);
        }

        return new SyntheticRepositoryState(rootDirectory, files);
    }

    public static async Task<SyntheticRepositoryState> MaterializeV2FromExistingAsync(
        SyntheticRepositoryDefinition definition,
        int seed,
        LocalDirectory sourceRootDirectory,
        LocalDirectory targetRootDirectory,
        IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encryption);

        var fileSystem = new RelativeFileSystem(targetRootDirectory);

        FileSystemHelper.CopyDirectory(sourceRootDirectory, targetRootDirectory);

        var files = new Dictionary<RelativePath, ContentHash>();
        foreach (var file in fileSystem.EnumerateFiles())
        {
            var relativePath = file.Path;

            await using var stream = fileSystem.OpenRead(relativePath);
            files[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        await ApplyV2MutationsAsync(definition, seed, targetRootDirectory, encryption, files);

        return new SyntheticRepositoryState(targetRootDirectory, files);
    }

    private static byte[] CreateBytes(int seed, string contentId, long sizeBytes)
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
        LocalDirectory rootDirectory,
        IEncryptionService encryption,
        Dictionary<RelativePath, ContentHash> files)
    {
        var fileSystem = new RelativeFileSystem(rootDirectory);

        foreach (var mutation in definition.V2Mutations)
        {
            switch (mutation.Kind)
            {
                case SyntheticFileMutationKind.Delete:
                    fileSystem.DeleteFile(mutation.Path);
                    files.Remove(mutation.Path);
                    break;

                case SyntheticFileMutationKind.Rename:
                    var targetRelativePath = mutation.TargetPath!.Value;
                    fileSystem.CreateDirectory(targetRelativePath.Parent ?? RelativePath.Root);
                    File.Move((rootDirectory / mutation.Path).ToString(), (rootDirectory / targetRelativePath).ToString());

                    var existingHash = files[mutation.Path];
                    files.Remove(mutation.Path);
                    files[targetRelativePath] = existingHash;
                    break;

                case SyntheticFileMutationKind.ChangeContent:
                case SyntheticFileMutationKind.Add:
                    var bytes = CreateBytes(seed, mutation.ReplacementContentId!, mutation.ReplacementSizeBytes!.Value);
                    await fileSystem.WriteAllBytesAsync(mutation.Path, bytes, CancellationToken.None);
                    files[mutation.Path] = encryption.ComputeHash(bytes);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
            }
        }
    }
}
