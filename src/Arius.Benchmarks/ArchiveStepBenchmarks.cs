using Arius.Core.Features.ArchiveCommand;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows;
using Arius.Tests.Shared.Fixtures;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ArchiveStepBenchmarks
{
    private RepositoryTestFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = await RepositoryTestFixture.CreateInMemoryAsync();

        var definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Representative);
        await SyntheticRepositoryMaterializer.MaterializeV1Async(
            definition,
            RepresentativeWorkflowCatalog.Canonical.Seed,
            _fixture.LocalRoot,
            _fixture.Encryption);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    [Benchmark(Description = "Archive_Step_V1_Representative_InMemory")]
    public async Task Archive_Step_V1_Representative_InMemory()
    {
        if (_fixture is null)
            throw new InvalidOperationException("Benchmark fixture was not initialized.");

        var result = await _fixture.CreateArchiveHandler()
            .Handle(
                new ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = _fixture.LocalRoot,
                }),
                CancellationToken.None)
            .AsTask();

        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "Archive benchmark failed.");
    }
}
