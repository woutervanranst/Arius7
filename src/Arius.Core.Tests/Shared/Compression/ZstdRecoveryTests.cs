using System.ComponentModel;
using System.Diagnostics;
using Arius.Core.Shared.Compression;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.Compression;

public class ZstdRecoveryTests
{
    [Test]
    public async Task FrameworkWrittenEncryptedChunk_IsRecoverableWithoutArius()
    {
        var python = Environment.GetEnvironmentVariable("ARIUS_TEST_PYTHON") ?? "python3";
        try
        {
            var (code, _) = await RunPythonAsync("-c", "import cryptography, zstandard");
            if (code != 0)
                Skip.Test("Recovery requires Python with cryptography and zstandard.");
        }
        catch (Win32Exception)
        {
            Skip.Test($"Recovery requires {python} on PATH.");
        }

        var directory = TestTempRoots.CreateDirectory("zstd-recovery");
        var files = new RelativeFileSystem(directory);
        var encryptedPath = RelativePath.Parse("chunk.enc");
        var recoveredPath = RelativePath.Parse("restored.bin");
        try
        {
            var original = new byte[256 * 1024];
            new Random(220).NextBytes(original);
            await using (var output = files.OpenOrCreateFile(encryptedPath, FileAccess.Write, FileShare.None))
            await using (var encryption = IEncryptionService.EncryptedInstance.WrapForEncryption(output))
            await using (var compression = new ZstdCompressionService().WrapForCompression(encryption))
                await compression.WriteAsync(original);

            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "recover-chunk.py")))
                root = root.Parent;
            root.ShouldNotBeNull("recover-chunk.py must be available above the test output.");

            var (exitCode, stderr) = await RunPythonAsync(
                Path.Combine(root.FullName, "recover-chunk.py"), directory.Resolve(encryptedPath), TestDefaults.Passphrase, directory.Resolve(recoveredPath));
            exitCode.ShouldBe(0, stderr);
            (await files.ReadAllBytesAsync(recoveredPath, CancellationToken.None)).ShouldBe(original);
        }
        finally
        {
            files.DeleteDirectory(RelativePath.Root, recursive: true);
        }

        async Task<(int ExitCode, string Stderr)> RunPythonAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            await stdout;
            return (process.ExitCode, await stderr);
        }
    }
}
