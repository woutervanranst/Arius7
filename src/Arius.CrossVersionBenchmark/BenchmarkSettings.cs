using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Arius.CrossVersionBenchmark;

/// <summary>
/// Process-wide configuration and shared services for the cross-version archive benchmark.
/// Populated once from env vars / args before BenchmarkDotNet runs, then read by the
/// in-process benchmark instances and the custom result columns.
/// </summary>
internal static class BenchmarkSettings
{
    public static string V5CliDll        { get; private set; } = "";
    public static string V7CliDll        { get; private set; } = "";
    public static string SourceDirectory { get; private set; } = "";
    public static string Account         { get; private set; } = "";
    public static string Key             { get; private set; } = "";
    public static string Passphrase      { get; private set; } = "";
    public static string Tier            { get; private set; } = "Cool";
    public static string ContainerPrefix { get; private set; } = "bench";
    public static string RunId           { get; private set; } = "";
    public static string OutputDirectory { get; private set; } = "";
    public static bool   Cleanup         { get; private set; } = true;

    public static string PristineRoot    { get; private set; } = "";
    public static int    SourceFileCount { get; private set; }
    public static long   SourceBytes     { get; private set; }

    public static BlobServiceClient BlobService { get; private set; } = null!;

    public static ConcurrentBag<string> CreatedContainers { get; } = new();

    public static void Initialize(IReadOnlyList<string> args)
    {
        V5CliDll        = Environment.GetEnvironmentVariable("ARIUS_V5_CLI") ?? "";
        V7CliDll        = Environment.GetEnvironmentVariable("ARIUS_V7_CLI") ?? "";
        SourceDirectory = "/Users/wouter/Downloads/AriusTest";
        Account         = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_NAME") ?? "";
        Key             = Environment.GetEnvironmentVariable("ARIUS_KEY") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_KEY") ?? "";
        Passphrase      = Environment.GetEnvironmentVariable("ARIUS_PASSPHRASE") ?? "ariusbench";
        RunId           = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        OutputDirectory = Path.Combine(AppContext.BaseDirectory, "results", RunId);

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--v5-cli":     V5CliDll = args[++i]; break;
                case "--v7-cli":     V7CliDll = args[++i]; break;
                case "--source":     SourceDirectory = args[++i]; break;
                case "--account":    Account = args[++i]; break;
                case "--key":        Key = args[++i]; break;
                case "--passphrase": Passphrase = args[++i]; break;
                case "--tier":       Tier = args[++i]; break;
                case "--prefix":     ContainerPrefix = args[++i]; break;
                case "--output":     OutputDirectory = args[++i]; break;
                case "--no-cleanup": Cleanup = false; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(V5CliDll)) throw new ArgumentException("v5 CLI path required (--v5-cli or ARIUS_V5_CLI).");
        if (string.IsNullOrWhiteSpace(V7CliDll)) throw new ArgumentException("v7 CLI path required (--v7-cli or ARIUS_V7_CLI).");
        if (string.IsNullOrWhiteSpace(Account))  throw new ArgumentException("account required (--account or ARIUS_ACCOUNT).");
        if (string.IsNullOrWhiteSpace(Key))      throw new ArgumentException("key required (--key or ARIUS_KEY).");
        if (!File.Exists(V5CliDll)) throw new FileNotFoundException("v5 CLI assembly not found.", V5CliDll);
        if (!File.Exists(V7CliDll)) throw new FileNotFoundException("v7 CLI assembly not found.", V7CliDll);

        Directory.CreateDirectory(OutputDirectory);

        BlobService = new BlobServiceClient(
            new Uri($"https://{Account}.blob.core.windows.net"),
            new StorageSharedKeyCredential(Account, Key));

        PristineRoot = Path.Combine(OutputDirectory, "pristine-source");
        PreparePristineSnapshot();
    }

    /// <summary>
    /// Copies the source folder once, keeping only real files (drops *.pointer.arius and .DS_Store)
    /// so the user's folder is never mutated and v5 never trips over v7-format pointers.
    /// </summary>
    static void PreparePristineSnapshot()
    {
        if (Directory.Exists(PristineRoot))
            Directory.Delete(PristineRoot, recursive: true);

        var count = 0;
        var bytes = 0L;
        foreach (var file in Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pointer.arius", StringComparison.OrdinalIgnoreCase) || name == ".DS_Store")
                continue;

            var target = Path.Combine(PristineRoot, Path.GetRelativePath(SourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
            bytes += new FileInfo(file).Length;
        }

        SourceFileCount = count;
        SourceBytes     = bytes;
    }

    public static void CopyPristineTo(string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);

        foreach (var file in Directory.EnumerateFiles(PristineRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(PristineRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Runs `archive` for the given version as a child process wrapped in `/usr/bin/time -l`
    /// (so we capture peak resident set size, in bytes on macOS). Throws on a non-zero exit.
    /// </summary>
    public static long RunArchive(string version, string container, string workingSource, string logPath)
    {
        var accountFlag = version == "v5" ? "--accountname" : "--account";
        var dll         = version == "v5" ? V5CliDll : V7CliDll;

        var psi = new ProcessStartInfo
        {
            FileName               = "/usr/bin/time",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var arg in new[]
                 {
                     "-l", "dotnet", dll, "archive", workingSource,
                     accountFlag, Account,
                     "-k", Key,
                     "--container", container,
                     "--passphrase", Passphrase,
                     "--tier", Tier,
                 })
            psi.ArgumentList.Add(arg);

        var output  = new StringBuilder();
        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        File.WriteAllText(logPath, output.ToString());

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{version} archive failed (exit {process.ExitCode}); see {logPath}");

        return ParseMaxRss(output.ToString());
    }

    static long ParseMaxRss(string timeOutput)
    {
        foreach (var line in timeOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith("maximum resident set size", StringComparison.Ordinal)
                && long.TryParse(trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var bytes))
                return bytes;
        }
        return 0;
    }

    public static long GetContainerBytes(string container)
    {
        var client = BlobService.GetBlobContainerClient(container);
        var total  = 0L;
        foreach (var blob in client.GetBlobs())
            total += blob.Properties.ContentLength ?? 0;
        return total;
    }

    public static void DeleteCreatedContainers()
    {
        foreach (var container in CreatedContainers.Distinct())
        {
            try { BlobService.DeleteBlobContainer(container); }
            catch (Exception ex) { Console.WriteLine($"  WARN could not delete {container}: {ex.Message}"); }
        }
    }
}
