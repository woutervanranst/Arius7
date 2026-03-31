using Arius.Core.Encryption;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Shouldly;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Recovery script compatibility tests: archive encrypted content → download raw blob →
/// decrypt with <c>recover-chunk.sh</c> → verify the output is byte-identical.
///
/// Requires Python3 with the 'cryptography' package and gunzip on PATH.
/// Skipped when those prerequisites are unavailable.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class OpenSslCompatibilityTests(AzuriteFixture azurite)
{
    private const string Passphrase = "openssl-compat-test";

    /// <summary>Absolute path to recover-chunk.sh in the repo root.</summary>
    private static string RecoverScript
    {
        get
        {
            // Walk up from the test assembly to find the repo root (contains recover-chunk.sh)
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir, "recover-chunk.sh");
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("recover-chunk.sh not found in any ancestor of " + AppContext.BaseDirectory);
        }
    }

    private static bool RecoverScriptAvailable()
    {
        try
        {
            // Needs python3 + cryptography + gunzip
            if (!CommandExists("python3") || !CommandExists("gunzip"))
                return false;

            var p = Process.Start(new ProcessStartInfo("python3", "-c \"from cryptography.hazmat.primitives.ciphers.aead import AESGCM\"")
            {
                RedirectStandardError = true,
                UseShellExecute       = false,
            });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("which", cmd)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static (int exitCode, string stdout, string stderr) RunScript(params string[] args)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        // bash <script> arg1 arg2 ...
        psi.ArgumentList.Add(RecoverScript);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var p = Process.Start(psi)!;
        p.WaitForExit(120_000);
        return (p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
    }

    // ── Large file encrypted → recover-chunk.sh decrypt → byte-identical ──────

    [Test]
    public async Task Archive_EncryptedLargeFile_RecoverScript_ByteIdentical()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.sh prerequisites not available — skipping");
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

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-recover-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "chunk.enc");
        var recoveredFile = Path.Combine(tempDir, "recovered.bin");

        Directory.CreateDirectory(tempDir);

        try
        {
            // Download the raw encrypted+gzipped blob
            {
                await using var downloadStream = await fix.BlobStorage.DownloadAsync(chunkBlobName);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress using recover-chunk.sh
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, recoveredFile);
            exitCode.ShouldBe(0, $"recover-chunk.sh failed: {stderr}");

            File.ReadAllBytes(recoveredFile).ShouldBe(original);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Encrypted tar bundle → recover-chunk.sh decrypt → tar extract ─────────

    [Test]
    public async Task Archive_EncryptedTarBundle_RecoverScript_FilesCorrect()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.sh prerequisites not available — skipping");
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

        // Find tar chunk blob
        var chunkBlobs = new List<string>();
        await foreach (var blob in fix.BlobStorage.ListAsync("chunks/"))
        {
            var meta = await fix.BlobStorage.GetMetadataAsync(blob);
            if (meta.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
                chunkBlobs.Add(blob);
        }
        chunkBlobs.Count.ShouldBe(1);

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-recover-tar-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "tar.enc");
        var tarFile       = Path.Combine(tempDir, "bundle.tar");

        Directory.CreateDirectory(tempDir);

        try
        {
            // Download raw blob
            {
                await using var downloadStream = await fix.BlobStorage.DownloadAsync(chunkBlobs[0]);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress to tar using recover-chunk.sh
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, tarFile);
            exitCode.ShouldBe(0, $"recover-chunk.sh failed: {stderr}");

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
            var enc   = new PassphraseEncryptionService(Passphrase);
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
