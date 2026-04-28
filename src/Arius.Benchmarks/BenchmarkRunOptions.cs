namespace Arius.Benchmarks;

internal sealed record BenchmarkRunOptions(
    string RepositoryRoot,
    string RawOutputRoot,
    string TailLogPath)
{
    public const int Iterations = 3;

    public static BenchmarkRunOptions Parse(IReadOnlyList<string> args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var defaultBenchmarkRoot = Path.Combine(repositoryRoot, "src", "Arius.Benchmarks");
        var defaultRawOutputRoot = Path.Combine(defaultBenchmarkRoot, "raw");

        var rawOutputRoot = defaultRawOutputRoot;
        var tailLogPath = Path.Combine(defaultBenchmarkRoot, "benchmark-tail.md");

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
            Path.GetFullPath(tailLogPath));
    }

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
        Console.WriteLine("  --raw-output <path>  Folder where per-run raw BenchmarkDotNet output is saved.");
        Console.WriteLine("  --tail-log <path>    Markdown benchmark tail log to append to.");
    }
}
