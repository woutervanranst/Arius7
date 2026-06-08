# Arius.AzureBlob.Benchmarks

Benchmarks for comparing Azure Blob container access patterns used by `AzureBlobContainerService`.

## What It Measures

The current benchmark suite compares three paths against one blob:

- `GetMetadataAsync`: one metadata-only request
- `TryDownloadAsync`: one download request that returns `null` on 404
- `DownloadIfExists`: `GetMetadataAsync` followed by `DownloadAsync`

`TryDownloadAsync` and `DownloadIfExists` both drain the returned stream to `Stream.Null` so the benchmark includes consuming the response body.

## Usage

Run from the repository root:

```powershell
dotnet run --project src\Arius.AzureBlob.Benchmarks\Arius.AzureBlob.Benchmarks.csproj -- --account-name <name> --account-key <key> --container-name <container> --blob-name <blob>
```

Arguments:

- `--account-name`: Azure storage account name
- `--account-key`: Azure storage account key
- `--container-name`: blob container name
- `--blob-name`: blob path to benchmark

Example:

```powershell
dotnet run --project src\Arius.AzureBlob.Benchmarks\Arius.AzureBlob.Benchmarks.csproj -- --account-name ariusci --account-key "<key>" --container-name test --blob-name chunk-index/8c
```

## Debug Builds

This benchmark runner uses:

```csharp
config.WithOptions(ConfigOptions.DisableOptimizationsValidator)
```

That allows running from a Debug build without BenchmarkDotNet rejecting the host configuration.

## Latest Results

Measured on:

- machine: Windows 11, Intel Core Ultra 7 165U
- runtime: `.NET 10.0.5`
- blob: `test/chunk-index/8c`

Results:

| Method | Mean | StdDev | Allocated |
| --- | ---: | ---: | ---: |
| `GetMetadataAsync` | `46.01 ms` | `1.316 ms` | `28.22 KB` |
| `TryDownloadAsync` | `45.84 ms` | `0.498 ms` | `29.97 KB` |
| `DownloadIfExists` | `91.03 ms` | `1.069 ms` | `55.59 KB` |

Observed takeaway:

- `GetMetadataAsync` and `TryDownloadAsync` were effectively tied for this blob
- `GetMetadataAsync` + `DownloadAsync` was roughly 2x the latency and about 2x the allocation

## BenchmarkDotNet Artifacts

Results are written to:

- `BenchmarkDotNet.Artifacts/results/Arius.AzureBlob.Benchmarks.AzureBlobContainerServiceBenchmarks-report.csv`
- `BenchmarkDotNet.Artifacts/results/Arius.AzureBlob.Benchmarks.AzureBlobContainerServiceBenchmarks-report-github.md`
- `BenchmarkDotNet.Artifacts/results/Arius.AzureBlob.Benchmarks.AzureBlobContainerServiceBenchmarks-report.html`
