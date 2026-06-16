using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Tests.Shared.Hashes;

public static class HashTestHelpers
{
    private static readonly PlaintextPassthroughService s_plaintext = new();

    // -- CONTENTHASH

    public static ContentHash ContentHashOf(ReadOnlySpan<byte> content)
        => ContentHashOf(content, s_plaintext);

    public static ContentHash ContentHashOf(string content)
        => ContentHashOf(content, s_plaintext);

    public static ContentHash ContentHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption)
        => encryption.ComputeHash(content);

    public static ContentHash ContentHashOf(string content, IEncryptionService encryption)
        => encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));


    // -- CHUNKHASH

    public static ChunkHash ChunkHashOf(ReadOnlySpan<byte> content)
        => ChunkHashOf(content, s_plaintext);

    public static ChunkHash ChunkHashOf(string content)
        => ChunkHashOf(content, s_plaintext);

    public static ChunkHash ChunkHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption)
        => ChunkHash.Parse(ContentHashOf(content, encryption));

    public static ChunkHash ChunkHashOf(string content, IEncryptionService encryption)
        => ChunkHash.Parse(ContentHashOf(content, encryption));


    // -- FILETREEHASH

    public static FileTreeHash FileTreeHashOf(ReadOnlySpan<byte> content)
        => FileTreeHashOf(content, s_plaintext);

    public static FileTreeHash FileTreeHashOf(string content)
        => FileTreeHashOf(content, s_plaintext);

    public static FileTreeHash FileTreeHashOf(ReadOnlySpan<byte> content, IEncryptionService encryption)
        => FileTreeHash.Parse(ContentHashOf(content, encryption));

    public static FileTreeHash FileTreeHashOf(string content, IEncryptionService encryption)
        => FileTreeHash.Parse(ContentHashOf(content, encryption));
}
