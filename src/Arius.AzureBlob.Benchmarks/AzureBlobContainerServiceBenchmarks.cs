using Arius.AzureBlob;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Azure.Storage;
using Azure.Storage.Blobs;
using BenchmarkDotNet.Attributes;

namespace Arius.AzureBlob.Benchmarks;

[MemoryDiagnoser]
public class AzureBlobContainerServiceBenchmarks
{
    private const string AccountNameEnvironmentVariable = "ARIUS_AZURE_BENCHMARK_ACCOUNT_NAME";
    private const string AccountKeyEnvironmentVariable = "ARIUS_AZURE_BENCHMARK_ACCOUNT_KEY";
    private const string ContainerNameEnvironmentVariable = "ARIUS_AZURE_BENCHMARK_CONTAINER_NAME";
    private const string BlobNameEnvironmentVariable = "ARIUS_AZURE_BENCHMARK_BLOB_NAME";

    public static AzureBlobContainerServiceBenchmarkOptions Options { get; set; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty);

    private AzureBlobContainerService? _service;
    private RelativePath _blobName;

    [GlobalSetup]
    public void Setup()
    {
        var options = GetOptions();

        var credential = new StorageSharedKeyCredential(options.AccountName, options.AccountKey);
        var serviceUri = new Uri($"https://{options.AccountName}.blob.core.windows.net");
        var container = new BlobServiceClient(serviceUri, credential)
            .GetBlobContainerClient(options.ContainerName);

        _service = new AzureBlobContainerService(container);
        _blobName = RelativePath.Parse(options.BlobName);
    }

    [Benchmark]
    public Task<BlobMetadata> GetMetadataAsync()
    {
        EnsureInitialized();
        return _service!.GetMetadataAsync(_blobName, CancellationToken.None);
    }

    [Benchmark]
    public async Task<string?> TryDownloadAsync()
    {
        EnsureInitialized();

        var download = await _service!.TryDownloadAsync(_blobName, CancellationToken.None);
        if (download is null)
            return null;

        await using var stream = download.Stream;
        await stream.CopyToAsync(Stream.Null, CancellationToken.None);
        return download.BlobIdentity;
    }

    [Benchmark]
    public async Task<string> DownloadIfExists()
    {
        EnsureInitialized();

        var metadata = await _service!.GetMetadataAsync(_blobName, CancellationToken.None);
        if (!metadata.Exists)
            throw new InvalidOperationException($"Blob '{_blobName}' does not exist.");

        var             download = await _service.DownloadAsync(_blobName, CancellationToken.None);
        await using var stream   = download.Stream;
        await stream.CopyToAsync(Stream.Null, CancellationToken.None);
        return download.BlobIdentity;
    }

    void EnsureInitialized()
    {
        if (_service is null)
            throw new InvalidOperationException("Benchmark state was not initialized.");
    }

    static AzureBlobContainerServiceBenchmarkOptions GetOptions()
    {
        var accountName = string.IsNullOrWhiteSpace(Options.AccountName)
            ? Environment.GetEnvironmentVariable(AccountNameEnvironmentVariable) ?? string.Empty
            : Options.AccountName;
        var accountKey = string.IsNullOrWhiteSpace(Options.AccountKey)
            ? Environment.GetEnvironmentVariable(AccountKeyEnvironmentVariable) ?? string.Empty
            : Options.AccountKey;
        var containerName = string.IsNullOrWhiteSpace(Options.ContainerName)
            ? Environment.GetEnvironmentVariable(ContainerNameEnvironmentVariable) ?? string.Empty
            : Options.ContainerName;
        var blobName = string.IsNullOrWhiteSpace(Options.BlobName)
            ? Environment.GetEnvironmentVariable(BlobNameEnvironmentVariable) ?? string.Empty
            : Options.BlobName;

        if (string.IsNullOrWhiteSpace(accountName)
            || string.IsNullOrWhiteSpace(accountKey)
            || string.IsNullOrWhiteSpace(containerName)
            || string.IsNullOrWhiteSpace(blobName))
        {
            throw new InvalidOperationException(
                "Provide --account-name, --account-key, --container-name, and --blob-name before running the benchmark.");
        }

        return new AzureBlobContainerServiceBenchmarkOptions(accountName, accountKey, containerName, blobName);
    }
}
