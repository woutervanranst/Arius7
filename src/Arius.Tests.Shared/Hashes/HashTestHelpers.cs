using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Tests.Shared.Hashes;

public static class HashTestHelpers
{
    // -- CONTENTHASH

    public static ContentHash ContentHashOf(ReadOnlySpan<byte> content)                                => ContentHashOf(content, IEncryptionService.PlaintextInstance);
    public static ContentHash ContentHashOf(string content)                                            => ContentHashOf(content, IEncryptionService.PlaintextInstance);
    public static ContentHash ContentHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption) => encryption.ComputeHash(content);
    public static ContentHash ContentHashOf(string content, IEncryptionService encryption)             => encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));


    // -- CHUNKHASH

    public static ChunkHash ChunkHashOf(ReadOnlySpan<byte> content)                                => ChunkHashOf(content, IEncryptionService.PlaintextInstance);
    public static ChunkHash ChunkHashOf(string content)                                            => ChunkHashOf(content, IEncryptionService.PlaintextInstance);
    public static ChunkHash ChunkHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption) => ChunkHash.Parse(ContentHashOf(content, encryption));
    public static ChunkHash ChunkHashOf(string content, IEncryptionService encryption)             => ChunkHash.Parse(ContentHashOf(content, encryption));


    // -- FILETREEHASH

    public static FileTreeHash FileTreeHashOf(ReadOnlySpan<byte> content)                                => FileTreeHashOf(content, IEncryptionService.PlaintextInstance);
    public static FileTreeHash FileTreeHashOf(string content)                                            => FileTreeHashOf(content, IEncryptionService.PlaintextInstance);
    public static FileTreeHash FileTreeHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption) => FileTreeHash.Parse(ContentHashOf(content, encryption));
    public static FileTreeHash FileTreeHashOf(string content, IEncryptionService encryption)             => FileTreeHash.Parse(ContentHashOf(content, encryption));
}