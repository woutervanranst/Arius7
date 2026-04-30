using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

public static class FileTreeBlobSerializer
{
    public static async Task<byte[]> SerializeForStorageAsync(
        FileTreeBlob tree,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default)
        => await FileTreeSerializer.SerializeForStorageAsync(tree.Entries, encryption, cancellationToken);

    public static async Task<FileTreeBlob> DeserializeFromStorageAsync(
        Stream source,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default)
        => new() { Entries = await FileTreeSerializer.DeserializeFromStorageAsync(source, encryption, cancellationToken) };

    public static FileTreeBlob Deserialize(byte[] bytes)
        => new() { Entries = FileTreeSerializer.Deserialize(bytes) };

    public static byte[] Serialize(FileTreeBlob tree)
        => FileTreeSerializer.Serialize(tree.Entries);

    public static string SerializeFileEntryLine(FileEntry entry)
        => FileTreeSerializer.SerializeFileEntryLine(entry);

    public static FileEntry ParseFileEntryLine(string line)
        => FileTreeSerializer.ParseFileEntryLine(line);

    public static FileTreeHash ComputeHash(FileTreeBlob tree, IEncryptionService encryption)
        => FileTreeSerializer.ComputeHash(tree.Entries, encryption);
}
