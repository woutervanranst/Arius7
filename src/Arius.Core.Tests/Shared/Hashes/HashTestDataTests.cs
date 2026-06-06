using System.Security.Cryptography;

namespace Arius.Core.Tests.Shared.Hashes;

public class HashTestDataTests
{
    [Test]
    public void CreateFileWithContentHashPrefix_CreatesFileWithMatchingHashPrefix()
    {
        return; 

        var path = @"C:\Users\WouterVanRanst\Downloads\Arius-Test-Source\new.txt";

        var hash = CreateFileWithContentHashPrefix(path, "8c");

        File.Exists(path).ShouldBeTrue();
        hash.ToString().ShouldStartWith("8c");
        ContentHash.FromDigest(SHA256.HashData(File.ReadAllBytes(path))).ShouldBe(hash);
    }
}
