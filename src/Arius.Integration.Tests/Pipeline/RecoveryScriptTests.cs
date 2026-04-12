using System.Diagnostics;
using System.Formats.Tar;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Pipeline.Fakes;
using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Tests for <c>recover-chunk.py</c>: archive encrypted content → download raw blob →
/// decrypt with the Python recovery script → verify the output is byte-identical.
///
/// Requires Python3 with the 'cryptography' package on PATH.
/// Skipped when those prerequisites are unavailable.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RecoveryScriptTests(AzuriteFixture azurite)
{
    private const string Passphrase = "recovery-script-test";

    /// <summary>Absolute path to recover-chunk.py in the repo root.</summary>
    private static string RecoverScript
    {
        get
        {
            // Walk up from the test assembly to find the repo root (contains recover-chunk.py)
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir, "recover-chunk.py");
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("recover-chunk.py not found in any ancestor of " + AppContext.BaseDirectory);
        }
    }

    private static bool RecoverScriptAvailable()
    {
        try
        {
            if (!CommandExists("python3"))
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
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(RecoverScript);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var p = Process.Start(psi)!;
        p.WaitForExit(120_000);
        return (p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
    }

    // ── CBC large file encrypted → recover-chunk.py → byte-identical ────────────

    [Test]
    public async Task Archive_CbcEncryptedLargeFile_RecoverScript_ByteIdentical()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.py prerequisites not available — skipping");
            return;
        }

        await using var fix = await PipelineFixture.CreateAsyncWithEncryption(
            azurite,
            new CbcEncryptionServiceAdapter(Passphrase));

        // 2 MB > threshold → large pipeline
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        fix.WriteFile("cbc-large.bin", original);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        // Find the chunk blob name — list chunks/ prefix
        var chunkBlobs = new List<string>();
        await foreach (var blob in fix.BlobContainer.ListAsync("chunks/"))
            chunkBlobs.Add(blob);
        chunkBlobs.Count.ShouldBe(1);
        var chunkBlobName = chunkBlobs[0];

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-recover-cbc-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "chunk.enc");
        var recoveredFile = Path.Combine(tempDir, "recovered.bin");

        Directory.CreateDirectory(tempDir);

        try
        {
            // Download the raw CBC-encrypted+gzipped blob
            {
                await using var downloadStream = await fix.BlobContainer.DownloadAsync(chunkBlobName);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress using recover-chunk.py (auto-detects Salted__ magic)
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, recoveredFile);
            exitCode.ShouldBe(0, $"recover-chunk.py failed: {stderr}");

            File.ReadAllBytes(recoveredFile).ShouldBe(original);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── GCM large file encrypted → recover-chunk.py → byte-identical ────────────

    [Test]
    public async Task Archive_EncryptedLargeFile_RecoverScript_ByteIdentical()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.py prerequisites not available — skipping");
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
        await foreach (var blob in fix.BlobContainer.ListAsync("chunks/"))
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
                await using var downloadStream = await fix.BlobContainer.DownloadAsync(chunkBlobName);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress using recover-chunk.py
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, recoveredFile);
            exitCode.ShouldBe(0, $"recover-chunk.py failed: {stderr}");

            File.ReadAllBytes(recoveredFile).ShouldBe(original);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── CBC tar bundle → recover-chunk.py → tar extract ──────────────────────────

    [Test]
    public async Task Archive_CbcEncryptedTarBundle_RecoverScript_FilesCorrect()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.py prerequisites not available — skipping");
            return;
        }

        await using var fix = await PipelineFixture.CreateAsyncWithEncryption(
            azurite,
            new CbcEncryptionServiceAdapter(Passphrase));

        // Small files → tar bundled
        var c1 = new byte[100]; Random.Shared.NextBytes(c1);
        var c2 = new byte[200]; Random.Shared.NextBytes(c2);
        fix.WriteFile("small1.txt", c1);
        fix.WriteFile("small2.txt", c2);

        var ar = await fix.ArchiveAsync();
        ar.Success.ShouldBeTrue(ar.ErrorMessage);

        // Find tar chunk blob
        var chunkBlobs = new List<string>();
        await foreach (var blob in fix.BlobContainer.ListAsync("chunks/"))
        {
            var meta = await fix.BlobContainer.GetMetadataAsync(blob);
            if (meta.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
                chunkBlobs.Add(blob);
        }
        chunkBlobs.Count.ShouldBe(1);

        var tempDir       = Path.Combine(Path.GetTempPath(), $"arius-recover-cbc-tar-{Guid.NewGuid():N}");
        var encryptedFile = Path.Combine(tempDir, "tar.enc");
        var tarFile       = Path.Combine(tempDir, "bundle.tar");

        Directory.CreateDirectory(tempDir);

        try
        {
            // Download raw blob
            {
                await using var downloadStream = await fix.BlobContainer.DownloadAsync(chunkBlobs[0]);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress to tar using recover-chunk.py (auto-detects Salted__ magic)
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, tarFile);
            exitCode.ShouldBe(0, $"recover-chunk.py failed: {stderr}");

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

    // ── GCM tar bundle → recover-chunk.py → tar extract ──────────────────────────

    [Test]
    public async Task Archive_EncryptedTarBundle_RecoverScript_FilesCorrect()
    {
        if (!RecoverScriptAvailable())
        {
            Skip.Unless(false, "recover-chunk.py prerequisites not available — skipping");
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
        await foreach (var blob in fix.BlobContainer.ListAsync("chunks/"))
        {
            var meta = await fix.BlobContainer.GetMetadataAsync(blob);
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
                await using var downloadStream = await fix.BlobContainer.DownloadAsync(chunkBlobs[0]);
                await using var fileStream     = File.Create(encryptedFile);
                await downloadStream.CopyToAsync(fileStream);
            }

            // Decrypt + decompress to tar using recover-chunk.py
            var (exitCode, _, stderr) = RunScript(encryptedFile, Passphrase, tarFile);
            exitCode.ShouldBe(0, $"recover-chunk.py failed: {stderr}");

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
