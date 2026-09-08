namespace Arius.Benchmarks;

/// <summary>Which benchmark class to run.</summary>
internal enum BenchmarkClass
{
    /// <summary>The end-to-end archive step against Azurite. Appends to the benchmark tail log.</summary>
    Archive,

    /// <summary>In-process allocation micro-benchmarks. No Docker; does not touch the tail log.</summary>
    Micro,
}

internal sealed record BenchmarkRunOptions(
    string RepositoryRoot,
    string RawOutputRoot,
    string TailLogPath,
    BenchmarkClass Class,
    string? Filter)
{
    public const int Iterations = 3;

    public static BenchmarkRunOptions Parse(IReadOnlyList<string> args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var defaultBenchmarkRoot = Path.Combine(repositoryRoot, "src", "Arius.Benchmarks");
        var defaultRawOutputRoot = Path.Combine(defaultBenchmarkRoot, "raw");

        var rawOutputRoot = defaultRawOutputRoot;
        var tailLogPath = Path.Combine(defaultBenchmarkRoot, "benchmark-tail.md");
        var benchmarkClass = BenchmarkClass.Archive;
        string? filter = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--raw-output":
                    rawOutputRoot = RequireValue(args, ref i, "--raw-output");
                    break;
                case "--tail-log":
                    tailLogPath = RequireValue(args, ref i, "--tail-log");
                    break;
                case "--class":
                    benchmarkClass = ParseClass(RequireValue(args, ref i, "--class"));
                    break;
                case "--filter":
                    filter = RequireValue(args, ref i, "--filter");
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option '{args[i]}'.");
            }
        }

        return new(
            repositoryRoot,
            Path.GetFullPath(rawOutputRoot),
            Path.GetFullPath(tailLogPath),
            benchmarkClass,
            filter);
    }

    static BenchmarkClass ParseClass(string value) => value.ToLowerInvariant() switch
    {
        "archive" => BenchmarkClass.Archive,
        "micro"   => BenchmarkClass.Micro,
        _         => throw new ArgumentException($"Unknown benchmark class '{value}'. Expected 'archive' or 'micro'."),
    };

    static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
            throw new ArgumentException($"{optionName} requires a value.");

        index++;
        return args[index];
    }

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git"))
                || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    static void PrintHelp()
    {
        Console.WriteLine("Runs the canonical representative workflow benchmark on Azurite.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --class <name>       'archive' (default, end-to-end on Azurite; needs Docker)");
        Console.WriteLine("                       or 'micro' (in-process allocation benchmarks).");
        Console.WriteLine("  --filter <glob>      Run only matching benchmarks, e.g. '*TarBuilder*' (micro only).");
        Console.WriteLine("  --raw-output <path>  Folder where per-run raw BenchmarkDotNet output is saved.");
        Console.WriteLine("  --tail-log <path>    Markdown benchmark tail log to append to (archive only).");
    }
}
