using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using Arius.Core.Encryption;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// OpenSSL compatibility tests: archive encrypted content → download raw blob →
/// decrypt with openssl CLI → verify.
///
/// Skipped when openssl is not on PATH (runs in CI only).
/// Covers tasks 15.4 and 15.5.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class OpenSslCompatibilityTests(AzuriteFixture azurite)
{
    private const string Passphrase = "openssl-compat-test";

    private static bool OpenSslAvailable()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("openssl", "version")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunOpenSsl(params string[] args)
    {
        var p = Process.Start(new ProcessStartInfo("openssl", string.Join(" ", args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        })!;
        p.WaitForExit(30_000);
        p.ExitCode.ShouldBe(0, $"openssl exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        return p.StandardOutput.ReadToEnd();
    }

    // ── 15.4: Large file encrypted → openssl decrypt → gunzip → byte-identical ─

    [Test]
    public async Task Archive_EncryptedLargeFile_OpensslDecrypt_ByteIdentical()
    {
        if (!OpenSslAvailable())
        {
            Skip.Unless(false, "openssl not on PATH — skipping");
            return;
        }

        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: Passphrase);

        // 2 MB > threshold → large pipeline
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        fix.WriteFile("large.bin", original);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        // Find the chunk blob name — list chunks/ prefix
        var chunkBlobs = new List<string>();
        await foreach (var blob in fix.BlobStorage.ListAsync("chunks/"))
            chunkBlobs.Add(blob);
        chunkBlobs.Count.ShouldBe(1);
        var chunkBlobName = chunkBlobs[0];

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-openssl-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "chunk.enc");
        var decryptedFile = Path.Combine(tempDir, "chunk.dec");
        var finalFile     = Path.Combine(tempDir, "original.bin");

        Directory.CreateDirectory(tempDir);

        try
        {
            // Download the raw encrypted+gzipped blob
            {
                await using var downloadStream = await fix.BlobStorage.DownloadAsync(chunkBlobName);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt with openssl (AES-256-CBC, PBKDF2 SHA-256 10K iterations)
            RunOpenSsl(
                "enc", "-d", "-aes-256-cbc", "-pbkdf2", "-iter", "10000",
                "-md", "sha256",
                "-pass", $"pass:{Passphrase}",
                "-in",  encryptedFile,
                "-out", decryptedFile);

            // Gunzip
            {
                await using var gz   = new GZipStream(File.OpenRead(decryptedFile), CompressionMode.Decompress);
                await using var out_ = File.Create(finalFile);
                await gz.CopyToAsync(out_);
            }

            File.ReadAllBytes(finalFile).ShouldBe(original);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── 15.5: Encrypted tar bundle → openssl decrypt → gunzip → tar extract ───

    [Test]
    public async Task Archive_EncryptedTarBundle_OpensslDecrypt_FilesCorrect()
    {
        if (!OpenSslAvailable())
        {
            Skip.Unless(false, "openssl not on PATH — skipping");
            return;
        }

        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: Passphrase);

        // Small files → tar bundled
        var c1 = new byte[100]; Random.Shared.NextBytes(c1);
        var c2 = new byte[200]; Random.Shared.NextBytes(c2);
        fix.WriteFile("small1.txt", c1);
        fix.WriteFile("small2.txt", c2);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        // Find tar chunk blob (large type = tar bundle, not thin)
        var chunkBlobs = new List<string>();
        await foreach (var blob in fix.BlobStorage.ListAsync("chunks/"))
        {
            var meta = await fix.BlobStorage.GetMetadataAsync(blob);
            if (meta.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
                chunkBlobs.Add(blob);
        }
        chunkBlobs.Count.ShouldBe(1);

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-openssl-tar-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "tar.enc");
        var decryptedFile = Path.Combine(tempDir, "tar.dec.tar.gz");
        var tarFile       = Path.Combine(tempDir, "bundle.tar");
        var extractDir    = Path.Combine(tempDir, "extracted");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            // Download raw blob
            {
                await using var downloadStream = await fix.BlobStorage.DownloadAsync(chunkBlobs[0]);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt
            RunOpenSsl(
                "enc", "-d", "-aes-256-cbc", "-pbkdf2", "-iter", "10000",
                "-md", "sha256",
                "-pass", $"pass:{Passphrase}",
                "-in",  encryptedFile,
                "-out", decryptedFile);

            // Gunzip
            {
                await using var gz    = new GZipStream(File.OpenRead(decryptedFile), CompressionMode.Decompress);
                await using var tarFs = File.Create(tarFile);
                await gz.CopyToAsync(tarFs);
            }

            // Read tar entries (named by content-hash)
            var extracted = new Dictionary<string, byte[]>();
            await using var tarStream = File.OpenRead(tarFile);
            var tarReader = new TarReader(tarStream);
            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(copyData: true)) is not null)
            {
                if (entry.DataStream is not null)
                {
                    using var ms = new MemoryStream();
                    await entry.DataStream.CopyToAsync(ms);
                    extracted[entry.Name] = ms.ToArray();
                }
            }

            // Verify both files are in the tar (by content-hash lookup)
            // Each entry is named by its content hash
            var enc  = new PassphraseEncryptionService(Passphrase);
            var hash1 = Convert.ToHexString(enc.ComputeHash(c1)).ToLowerInvariant();
            var hash2 = Convert.ToHexString(enc.ComputeHash(c2)).ToLowerInvariant();

            extracted.ShouldContainKey(hash1);
            extracted.ShouldContainKey(hash2);
            extracted[hash1].ShouldBe(c1);
            extracted[hash2].ShouldBe(c2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
