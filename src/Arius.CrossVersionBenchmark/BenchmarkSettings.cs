using System.Diagnostics;
using System.Globalization;
using System.Text;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Arius.CrossVersionBenchmark;

/// <summary>
/// Configuration and shared services for the cross-version archive benchmark.
///
/// BenchmarkDotNet runs the benchmark in a child process (out-of-process toolchain, required for
/// these multi-minute runs), so configuration travels as environment variables and is re-parsed by
/// <see cref="Parse"/> in the child's <c>[GlobalSetup]</c>. Per-iteration metrics (peak memory, blob
/// size) cross the process boundary as CSV files the host reads when rendering the result columns.
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

    /// <summary>When true, the benchmark containers are left in place after the run for inspection.</summary>
    public static bool   KeepContainers  { get; private set; } = true;

    public static int  IterationCount    => 3;

    public static int  SourceFileCount   { get; private set; }
    public static long SourceBytes       { get; private set; }

    public static BlobServiceClient BlobService { get; private set; } = null!;

    static readonly List<string> CreatedContainers = new();

    /// <summary>
    /// Reads configuration from environment variables, then applies any command-line overrides.
    /// Cheap: validates inputs but does no IO. Safe to call in both the host and the child.
    /// </summary>
    public static void Parse(IReadOnlyList<string> args)
    {
        V5CliDll        = Environment.GetEnvironmentVariable("ARIUS_V5_CLI") ?? "";
        V7CliDll        = Environment.GetEnvironmentVariable("ARIUS_V7_CLI") ?? "";
        SourceDirectory = Environment.GetEnvironmentVariable("ARIUS_SOURCE") ?? "/Users/wouter/Downloads/AriusTest";
        Account         = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_NAME") ?? "";
        Key             = Environment.GetEnvironmentVariable("ARIUS_KEY") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_KEY") ?? "";
        Passphrase      = Environment.GetEnvironmentVariable("ARIUS_PASSPHRASE") ?? "ariusbench";
        Tier            = Environment.GetEnvironmentVariable("ARIUS_TIER") ?? "Cool";
        ContainerPrefix = Environment.GetEnvironmentVariable("ARIUS_PREFIX") ?? "bench";
        RunId           = Environment.GetEnvironmentVariable("ARIUS_RUNID") ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        OutputDirectory = Environment.GetEnvironmentVariable("ARIUS_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "results", RunId);
        KeepContainers  = Environment.GetEnvironmentVariable("ARIUS_KEEP") != "0"; // default: keep

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
                case "--keep":         KeepContainers = true; break;
                case "--delete-after": KeepContainers = false; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(V5CliDll)) throw new ArgumentException("v5 CLI path required (--v5-cli or ARIUS_V5_CLI).");
        if (string.IsNullOrWhiteSpace(V7CliDll)) throw new ArgumentException("v7 CLI path required (--v7-cli or ARIUS_V7_CLI).");
        if (string.IsNullOrWhiteSpace(Account))  throw new ArgumentException("account required (--account or ARIUS_ACCOUNT).");
        if (string.IsNullOrWhiteSpace(Key))      throw new ArgumentException("key required (--key or ARIUS_KEY).");

        // Absolutize paths in the host (where the relative form resolves correctly) so the
        // BenchmarkDotNet child process — which runs from a different working directory — sees
        // valid paths via the environment variables.
        V5CliDll        = Path.GetFullPath(V5CliDll);
        V7CliDll        = Path.GetFullPath(V7CliDll);
        SourceDirectory = Path.GetFullPath(SourceDirectory);
        OutputDirectory = Path.GetFullPath(OutputDirectory);

        if (!File.Exists(V5CliDll)) throw new FileNotFoundException("v5 CLI assembly not found.", V5CliDll);
        if (!File.Exists(V7CliDll)) throw new FileNotFoundException("v7 CLI assembly not found.", V7CliDll);
    }

    /// <summary>Environment variables to hand to the BenchmarkDotNet child process.</summary>
    public static IEnumerable<(string Key, string Value)> ToEnvironment() =>
    [
        ("ARIUS_V5_CLI", V5CliDll),
        ("ARIUS_V7_CLI", V7CliDll),
        ("ARIUS_SOURCE", SourceDirectory),
        ("ARIUS_ACCOUNT", Account),
        ("ARIUS_KEY", Key),
        ("ARIUS_PASSPHRASE", Passphrase),
        ("ARIUS_TIER", Tier),
        ("ARIUS_PREFIX", ContainerPrefix),
        ("ARIUS_RUNID", RunId),
        ("ARIUS_OUTPUT", OutputDirectory),
        ("ARIUS_KEEP", KeepContainers ? "1" : "0"),
    ];

    /// <summary>Creates the storage client from the resolved account/key. Cheap; no dataset IO.</summary>
    public static void EnsureBlobService()
        => BlobService = new BlobServiceClient(
            new Uri($"https://{Account}.blob.core.windows.net"),
            new StorageSharedKeyCredential(Account, Key));

    /// <summary>Heavy, IO-bound init: storage client + the pristine dataset snapshot. Child-only.</summary>
    public static void BuildHeavyState()
    {
        Directory.CreateDirectory(OutputDirectory);
        EnsureBlobService();
        PreparePristineSnapshot();
    }

    /// <summary>
    /// Copies the source folder once, keeping only real files (drops *.pointer.arius and .DS_Store)
    /// so the user's folder is never mutated and v5 never trips over v7-format pointers.
    /// </summary>
    static void PreparePristineSnapshot()
    {
        var pristine = PristineRoot;
        if (Directory.Exists(pristine))
            Directory.Delete(pristine, recursive: true);

        var count = 0;
        var bytes = 0L;
        foreach (var file in Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pointer.arius", StringComparison.OrdinalIgnoreCase) || name == ".DS_Store")
                continue;

            var target = Path.Combine(pristine, Path.GetRelativePath(SourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
            bytes += new FileInfo(file).Length;
        }

        SourceFileCount = count;
        SourceBytes     = bytes;
    }

    static string PristineRoot => Path.Combine(OutputDirectory, "pristine-source");

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

    public static string NewContainerName(string version, int iteration)
    {
        var name = $"{ContainerPrefix}-{version}-{RunId}-{iteration}";
        lock (CreatedContainers) CreatedContainers.Add(name);
        return name;
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

    // ── Cross-process metrics (one CSV per version) ──────────────────────────

    static string MetricsFile(string version) => Path.Combine(OutputDirectory, $"metrics-{version}.csv");

    public static void AppendMetric(string version, long peakRssBytes, long blobBytes)
        => File.AppendAllText(MetricsFile(version), $"{peakRssBytes},{blobBytes},{SourceBytes}\n");

    /// <summary>Reads a version's samples: returns (mean peak RSS, mean blob, source bytes) or null if none.</summary>
    public static (double PeakRss, double Blob, long Source)? ReadMetrics(string version)
    {
        var file = MetricsFile(version);
        if (!File.Exists(file))
            return null;

        var rows = File.ReadAllLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .ToList();
        if (rows.Count == 0)
            return null;

        return (rows.Average(r => double.Parse(r[0], CultureInfo.InvariantCulture)),
                rows.Average(r => double.Parse(r[1], CultureInfo.InvariantCulture)),
                long.Parse(rows[0][2], CultureInfo.InvariantCulture));
    }

    // ── Container cleanup ────────────────────────────────────────────────────

    public static IReadOnlyList<string> ListCreatedContainers()
    {
        lock (CreatedContainers) return CreatedContainers.Distinct().ToList();
    }

    public static void DeleteCreatedContainers()
    {
        foreach (var container in ListCreatedContainers())
            TryDelete(container);
    }

    public static int DeleteContainersWithPrefix(string prefix)
    {
        var deleted = 0;
        foreach (var item in BlobService.GetBlobContainers(prefix: prefix))
        {
            TryDelete(item.Name);
            deleted++;
        }
        return deleted;
    }

    static void TryDelete(string container)
    {
        try { BlobService.DeleteBlobContainer(container); Console.WriteLine($"  deleted {container}"); }
        catch (Exception ex) { Console.WriteLine($"  WARN could not delete {container}: {ex.Message}"); }
    }
}
