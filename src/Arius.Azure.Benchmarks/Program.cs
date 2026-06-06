using BenchmarkDotNet.Running;

namespace Arius.Azure.Benchmarks;

internal static class Program
{
    static void Main(string[] args)
    {
        AzureBlobContainerServiceBenchmarks.Options = AzureBlobContainerServiceBenchmarkOptions.Parse(args);
        BenchmarkRunner.Run<AzureBlobContainerServiceBenchmarks>();
    }
}
