using Arius.Core.Shared.Encryption;
using Shouldly;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Arius.Core.Tests.Encryption;

/// <summary>
/// Golden file tests: decrypt actual chunks produced by the previous Arius version.
/// These tests verify backwards compatibility with encrypted blobs already in archive storage.
///
/// Golden files live in Arius.Core.Tests/Encryption/GoldenFiles/ and are copied to the
/// output directory via the CopyToOutputDirectory MSBuild item in the .csproj.
///
/// Two chunks were produced by the previous Arius version with passphrase "wouter":
///
///   9ffc39c1… — large chunk: gzip'd PNG (Lena, 512×512 RGB)
///   2552b810… — tar chunk:   gzip'd tar of two small files
///                               4e1c18b5… → "world" (5 bytes)
///                               3eba035d… → "42"    (2 bytes)
///
/// The filename of each chunk (and each tar entry) is SHA256("wouter" + plaintext_bytes).
/// </summary>
public class GoldenFileDecryptionTests
{
    private const string Passphrase = "wouter";

    private static readonly string GoldenFilesDir =
        Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles");

    // ── File names ──────────────────────────────────────────────────────────────

    private const string LargeChunkFile =
        "9ffc39c119e735c3c96e5ee912132a52c9c98566fb2a7c2ef156c4666afab18d";

    private const string TarChunkFile =
        "2552b810aee26966d3a50445d390f5a512591c33085f7d7e3eb8ae0b407c82a0";

    // ── Expected hashes ─────────────────────────────────────────────────────────

    /// <summary>Plain SHA256 of the decompressed Lena PNG bytes (no passphrase).</summary>
    private const string LenaPlaintextSha256 =
        "7e497501a28bcf9a353ccadf6eb9216bf098ac32888fb542fb9bfe71d486761f";

    /// <summary>SHA256("wouter" + lena_bytes) — must equal the chunk filename.</summary>
    private const string LenaContentHash =
        "9ffc39c119e735c3c96e5ee912132a52c9c98566fb2a7c2ef156c4666afab18d";

    /// <summary>SHA256("wouter" + "world") — must equal the tar entry name for hello.txt.</summary>
    private const string WorldContentHash =
        "4e1c18b56cca9e5c2f27065adddce7585ba114de38a38ec2a6bf54514783ee1b";

    /// <summary>SHA256("wouter" + "42") — must equal the tar entry name for answer.txt.</summary>
    private const string FortyTwoContentHash =
        "3eba035d48fdc6a6292991904c64c2f607b1d07c4ae47704f4e74f2259682e93";

    // ── Helper ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts (AES-256-CBC, openssl-compatible) then gunzips a golden file,
    /// returning the raw plaintext bytes.
    /// </summary>
    private static async Task<byte[]> DecryptAndDecompressAsync(string fileName)
    {
        var path = Path.Combine(GoldenFilesDir, fileName);
        File.Exists(path).ShouldBeTrue($"Golden file not found: {path}");

        var svc = new PassphraseEncryptionService(Passphrase);

        await using var fs          = File.OpenRead(path);
        await using var decStream   = svc.WrapForDecryption(fs);
        await using var gzipStream  = new GZipStream(decStream, CompressionMode.Decompress);

        var ms = new MemoryStream();
        await gzipStream.CopyToAsync(ms);
        ms.Position = 0;
        return ms.ToArray();
    }

    // ── Test A: large chunk → decrypt → verify plaintext SHA256 ─────────────────

    /// <summary>
    /// Restoring the large chunk produces the exact original Lena PNG bytes.
    /// Verifies the AES-CBC + PBKDF2-SHA256 decryption and gzip decompression
    /// are backwards-compatible with the previous Arius version.
    /// </summary>
    [Test]
    public async Task LargeChunk_Decrypt_PlaintextMatchesExpectedSha256()
    {
        var plaintext = await DecryptAndDecompressAsync(LargeChunkFile);

        var hashHex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        hashHex.ShouldBe(LenaPlaintextSha256);
    }

    // ── Test B: large chunk → re-archive hash == chunk filename ─────────────────

    /// <summary>
    /// Hashing the Lena plaintext with the v7 formula — SHA256(passphrase + data) —
    /// reproduces the original chunk filename exactly.
    /// Verifies that re-archiving the same file would produce the same content hash,
    /// enabling deduplication against existing archive blobs.
    /// </summary>
    [Test]
    public async Task LargeChunk_ContentHash_MatchesChunkFilename()
    {
        var plaintext = await DecryptAndDecompressAsync(LargeChunkFile);

        var svc     = new PassphraseEncryptionService(Passphrase);
        var hash    = svc.ComputeHash(plaintext);
        var hashHex = Convert.ToHexString(hash).ToLowerInvariant();

        hashHex.ShouldBe(LenaContentHash);
    }

    // ── Test C: tar chunk → decrypt → verify entry contents ─────────────────────

    /// <summary>
    /// Restoring the tar chunk produces the correct raw bytes for each entry.
    /// Entries are identified by their content hash (filenames are not stored).
    /// </summary>
    [Test]
    public async Task TarChunk_Decrypt_EntryContentsMatchExpected()
    {
        var path = Path.Combine(GoldenFilesDir, TarChunkFile);
        File.Exists(path).ShouldBeTrue($"Golden file not found: {path}");

        var svc = new PassphraseEncryptionService(Passphrase);

        await using var fs         = File.OpenRead(path);
        await using var decStream  = svc.WrapForDecryption(fs);
        await using var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        TarEntry? entry;
        while ((entry = await tarReader.GetNextEntryAsync(copyData: true)) is not null)
        {
            entry.DataStream.ShouldNotBeNull();
            var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms);
            entries[entry.Name] = ms.ToArray();
        }

        entries.Count.ShouldBe(2);

        entries.ShouldContainKey(WorldContentHash);
        entries[WorldContentHash].ShouldBe("world"u8.ToArray());

        entries.ShouldContainKey(FortyTwoContentHash);
        entries[FortyTwoContentHash].ShouldBe("42"u8.ToArray());
    }

    // ── Test D: tar entry hashes == entry names (re-archive produces same hashes) ─

    /// <summary>
    /// Hashing each entry's plaintext with the v7 formula produces the hash that
    /// was used as the tar entry name in the previous version.
    /// Verifies that re-archiving these files would generate the identical content
    /// hashes, enabling deduplication against existing archive blobs.
    /// </summary>
    [Test]
    public void TarChunk_EntryContentHashes_MatchEntryNames()
    {
        var svc = new PassphraseEncryptionService(Passphrase);

        var worldHash    = Convert.ToHexString(svc.ComputeHash("world"u8.ToArray())).ToLowerInvariant();
        var fortyTwoHash = Convert.ToHexString(svc.ComputeHash("42"u8.ToArray())).ToLowerInvariant();

        worldHash.ShouldBe(WorldContentHash);
        fortyTwoHash.ShouldBe(FortyTwoContentHash);
    }
}
