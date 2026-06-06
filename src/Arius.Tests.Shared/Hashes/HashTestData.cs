using System.Buffers.Binary;
using System.Security.Cryptography;
using Arius.Core.Shared.Hashes;

namespace Arius.Tests.Shared.Hashes;

public static class HashTestData
{
    public static ContentHash  FakeContentHash(char c)  => ContentHash.Parse(new string(c,  64));
    public static ChunkHash    FakeChunkHash(char c)    => ChunkHash.Parse(new string(c,    64));
    public static FileTreeHash FakeFileTreeHash(char c) => FileTreeHash.Parse(new string(c, 64));

    public static ContentHash CreateFileWithContentHashPrefix(string path, string hashPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashPrefix);

        hashPrefix = NormalizePrefix(hashPrefix);

        var parentDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        for (long attempt = 0; ; attempt++)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, attempt);
            var hash = ContentHash.FromDigest(SHA256.HashData(bytes));

            if (!hash.ToString().StartsWith(hashPrefix, StringComparison.Ordinal))
                continue;

            File.WriteAllBytes(path, bytes);
            return hash;
        }

        static string NormalizePrefix(string hashPrefix)
        {
            if (hashPrefix.Length > 4)
                throw new ArgumentOutOfRangeException(nameof(hashPrefix), hashPrefix, $"Generated hash prefixes are limited to 4 hex characters to keep tests fast.");

            Span<char> chars = stackalloc char[hashPrefix.Length];
            for (var i = 0; i < hashPrefix.Length; i++)
            {
                var c = hashPrefix[i];
                chars[i] = c switch
                {
                    >= '0' and <= '9' => c,
                    >= 'a' and <= 'f' => c,
                    >= 'A' and <= 'F' => char.ToLowerInvariant(c),
                    _                 => throw new FormatException($"Invalid hex character '{c}'.")
                };
            }

            return new string(chars);
        }
    }
}
