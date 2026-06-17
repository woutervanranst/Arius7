using System.Security.Cryptography;
using System.Text;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class FakeChunkGenerator
{
    [Test]
    public void CreateFileWithContentHashPrefix()
    {
        return;

        //var path = @"C:\Users\WouterVanRanst\Downloads\Arius-Test-Source\new.txt";
        //var hash = CreateFileWithContentHashPrefix(path, "8c");
        //File.Exists(path).ShouldBeTrue();
        //hash.ToString().ShouldStartWith("8c");
        //ContentHash.FromDigest(SHA256.HashData(File.ReadAllBytes(path))).ShouldBe(hash);


        const string hashPrefix = "8ccc";
        var hashes = new HashSet<ContentHash>();

        for (var i = 21; i < 40; i++)
        {
            var path = $@"C:\Users\WouterVanRanst\Downloads\Arius-Test-Source2\new-{i:00}.txt";
            var hash = CreateFileWithContentHashPrefix(path, hashPrefix);

            File.Exists(path).ShouldBeTrue();
            new FileInfo(path).Length.ShouldBeGreaterThan(4096);
            hash.ToString().ShouldStartWith(hashPrefix);
            ContentHash.FromDigest(SHA256.HashData(File.ReadAllBytes(path))).ShouldBe(hash);
            hashes.Add(hash).ShouldBeTrue();
        }

        hashes.Count.ShouldBe(20);
    }

    private static ContentHash CreateFileWithContentHashPrefix(string path, string hashPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashPrefix);

        hashPrefix = NormalizePrefix(hashPrefix);

        var parentDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        for (long attempt = 0; ; attempt++)
        {
            var bytes = Encoding.UTF8.GetBytes($"Arius test content for {Path.GetFullPath(path)}; attempt {attempt};\n" + new string('x', 4096));
            var hash  = ContentHash.FromDigest(SHA256.HashData(bytes));

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
