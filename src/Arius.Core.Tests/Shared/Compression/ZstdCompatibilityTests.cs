using Arius.Core.Shared.Compression;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.Compression;

public class ZstdCompatibilityTests
{
    [Test]
    public async Task ExistingEncryptedZstdGoldenChunk_RemainsReadable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles",
            "b886c2f18e3a1be5bfef76821ca1751126c6534ac267dd539cbbabe09801682f");
        var encryption = IEncryptionService.EncryptedInstance;
        await using var encrypted = File.OpenRead(path);
        await using var decrypted = encryption.WrapForDecryption(encrypted);
        await using var decompressed = new ZstdCompressionService().WrapForDecompression(decrypted);
        using var output = new MemoryStream();
        await decompressed.CopyToAsync(output);

        output.ToArray().ShouldBe("Hello, zstd golden file!"u8.ToArray());
        encryption.ComputeHash(output.ToArray()).ToString().ShouldBe(Path.GetFileName(path));
    }
}
