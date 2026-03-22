using Arius.Core.Encryption;
using Shouldly;

namespace Arius.Core.Tests.Encryption;

/// <summary>
/// Golden file tests: decrypt actual chunks produced by the previous Arius version.
/// These tests verify backwards compatibility with encrypted blobs already in archive storage.
///
/// To add a golden file test:
/// 1. Download a real encrypted chunk from archive storage (or produce one with the old Arius tool).
/// 2. Place it under Arius.Core.Tests/Encryption/GoldenFiles/
/// 3. Record its known plaintext (or plaintext SHA256) and passphrase.
/// 4. Add a test case below following the pattern of the example.
///
/// These tests are skipped by default until golden files are added.
/// </summary>
public class GoldenFileDecryptionTests
{
    private static readonly string GoldenFilesDir =
        Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles");

    /// <summary>
    /// Example skeleton — replace with a real golden file + passphrase + expected hash.
    /// </summary>
    [Test]
    [Skip("No golden files checked in yet — add a real encrypted chunk to GoldenFiles/ to enable")]
    public async Task Decrypt_GoldenChunk_ProducesExpectedContent()
    {
        const string passphrase    = "REPLACE_WITH_REAL_PASSPHRASE";
        const string fileName      = "REPLACE_WITH_REAL_CHUNK_FILE";
        const string expectedSha256Hex = "REPLACE_WITH_EXPECTED_PLAINTEXT_SHA256_HEX";

        var path = Path.Combine(GoldenFilesDir, fileName);
        File.Exists(path).ShouldBeTrue($"Golden file not found: {path}");

        var svc = new PassphraseEncryptionService(passphrase);

        await using var fs = File.OpenRead(path);
        var decStream = svc.WrapForDecryption(fs);

        var plaintext = new PlaintextPassthroughService();
        var hash = await plaintext.ComputeHashAsync(decStream);
        var hashHex = Convert.ToHexString(hash).ToLowerInvariant();

        hashHex.ShouldBe(expectedSha256Hex);
    }
}
