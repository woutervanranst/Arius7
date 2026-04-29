using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Encryption;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.IO;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ArchiveStepBenchmarks
{
    private AzuriteE2EBackendFixture? _backend;
    private SyntheticRepositoryDefinition? _definition;
    private string? _preparedSourceRoot;
    private E2EStorageBackendContext? _context;
    private RepositoryTestFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _backend = new AzuriteE2EBackendFixture();
        await _backend.InitializeAsync();

        _definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Representative);
        _preparedSourceRoot = Path.Combine(Path.GetTempPath(), "arius", $"benchmark-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_preparedSourceRoot);

        await SyntheticRepositoryMaterializer.MaterializeV1Async(
            _definition,
            RepresentativeWorkflowCatalog.Canonical.Seed,
            _preparedSourceRoot,
            new PassphraseEncryptionService("arius-test-passphrase"));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_backend is not null)
            await _backend.DisposeAsync();

        if (_preparedSourceRoot is not null && Directory.Exists(_preparedSourceRoot))
            Directory.Delete(_preparedSourceRoot, recursive: true);
    }

    [IterationSetup]
    public void IterationSetup()
        => IterationSetupCoreAsync().GetAwaiter().GetResult();

    async Task IterationSetupCoreAsync()
    {
        if (_backend is null || _preparedSourceRoot is null)
            throw new InvalidOperationException("Benchmark state was not initialized.");

        _context = await _backend.CreateContextAsync();
        _fixture = await RepositoryTestFixture.CreateWithPassphraseAsync(
            _context.BlobContainer,
            _context.AccountName,
            _context.ContainerName);

        if (Directory.Exists(_fixture.LocalRoot))
            Directory.Delete(_fixture.LocalRoot, recursive: true);

        FileSystemHelper.CopyDirectory(_preparedSourceRoot, _fixture.LocalRoot);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => IterationCleanupCoreAsync().GetAwaiter().GetResult();

    async Task IterationCleanupCoreAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
            _fixture = null;
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
            _context = null;
        }
    }

    [Benchmark(Description = "Archive_Step_V1_Representative_Azurite")]
    public async Task Archive_Step_V1_Representative_Azurite()
    {
        if (_fixture is null)
            throw new InvalidOperationException("Benchmark iteration state was not initialized.");

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
