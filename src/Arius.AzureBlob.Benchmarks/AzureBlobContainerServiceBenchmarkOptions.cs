namespace Arius.AzureBlob.Benchmarks;

public sealed record AzureBlobContainerServiceBenchmarkOptions(
    string AccountName,
    string AccountKey,
    string ContainerName,
    string BlobName)
{
    public static AzureBlobContainerServiceBenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        string? accountName = null;
        string? accountKey = null;
        string? containerName = null;
        string? blobName = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--account-name":
                    accountName = RequireValue(args, ref i, "--account-name");
                    break;
                case "--account-key":
                    accountKey = RequireValue(args, ref i, "--account-key");
                    break;
                case "--container-name":
                    containerName = RequireValue(args, ref i, "--container-name");
                    break;
                case "--blob-name":
                    blobName = RequireValue(args, ref i, "--blob-name");
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option '{args[i]}'.");
            }
        }

        return new AzureBlobContainerServiceBenchmarkOptions(
            AccountName: accountName ?? string.Empty,
            AccountKey: accountKey ?? string.Empty,
            ContainerName: containerName ?? string.Empty,
            BlobName: blobName ?? string.Empty);
    }

    static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
            throw new ArgumentException($"{optionName} requires a value.");

        index++;
        return args[index];
    }

    static void PrintHelp()
    {
        Console.WriteLine("Runs AzureBlobContainerService metadata vs try-download benchmarks.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --account-name <name>      Azure storage account name.");
        Console.WriteLine("  --account-key <key>        Azure storage account key.");
        Console.WriteLine("  --container-name <name>    Blob container name.");
        Console.WriteLine("  --blob-name <path>         Blob name to benchmark.");
    }
}
