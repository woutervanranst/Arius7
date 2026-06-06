using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Arius.AzureBlob.Benchmarks;

internal static class Program
{
    static void Main(string[] args)
    {
        AzureBlobContainerServiceBenchmarks.Options = AzureBlobContainerServiceBenchmarkOptions.Parse(args);
        Environment.SetEnvironmentVariable("ARIUS_AZURE_BENCHMARK_ACCOUNT_NAME", AzureBlobContainerServiceBenchmarks.Options.AccountName);
        Environment.SetEnvironmentVariable("ARIUS_AZURE_BENCHMARK_ACCOUNT_KEY", AzureBlobContainerServiceBenchmarks.Options.AccountKey);
        Environment.SetEnvironmentVariable("ARIUS_AZURE_BENCHMARK_CONTAINER_NAME", AzureBlobContainerServiceBenchmarks.Options.ContainerName);
        Environment.SetEnvironmentVariable("ARIUS_AZURE_BENCHMARK_BLOB_NAME", AzureBlobContainerServiceBenchmarks.Options.BlobName);

        var config = ManualConfig
            .Create(DefaultConfig.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkRunner.Run<AzureBlobContainerServiceBenchmarks>(config);
    }
}
