using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class RepresentativeWorkflowBenchmarks
{
    AzuriteE2EBackendFixture? _backend;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _backend = new();
        await _backend.InitializeAsync();

        await using var context = await _backend.CreateContextAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_backend is not null)
            await _backend.DisposeAsync();
    }

    [Benchmark(Description = "Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)")]
    public async Task Canonical_Representative_Workflow_Runs_On_Supported_Backends_Azurite()
    {
        if (_backend is null)
            throw new InvalidOperationException("Benchmark backend was not initialized.");

        var result = await RepresentativeWorkflowRunner.RunAsync(
            _backend,
            RepresentativeWorkflowCatalog.Canonical);

        if (result.WasSkipped)
            throw new InvalidOperationException(result.SkipReason ?? "Representative workflow benchmark was skipped.");

        if (_backend.Capabilities.SupportsArchiveTier && result.ArchiveTierOutcome is null)
            throw new InvalidOperationException("Archive tier outcome was not captured for an archive-tier-capable backend.");
    }
}